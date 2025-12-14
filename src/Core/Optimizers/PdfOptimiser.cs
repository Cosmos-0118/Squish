using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

/// <summary>
/// Windows port of <c>PDF.swift</c>: shells out to Ghostscript with the exact argument stack used on macOS,
/// reports progress per page, and enforces size + metadata parity.
/// </summary>
public sealed class PdfOptimiser : IOptimiser
{
    private readonly PdfOptimiserOptions _options;
    private readonly IPdfToolchain _toolchain;

    public PdfOptimiser(PdfOptimiserOptions? options = null, IPdfToolchain? toolchain = null)
    {
        _options = options ?? PdfOptimiserOptions.Default;
        _toolchain = toolchain ?? new ExternalPdfToolchain(_options);
    }

    public ItemType ItemType => ItemType.Pdf;

    public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var source = request.SourcePath;
        if (!File.Exists(source.Value))
        {
            return OptimisationResult.Failure(request.RequestId, $"Source file not found: {source.Value}");
        }

        if (!MediaFormats.IsPdf(source))
        {
            return OptimisationResult.Unsupported(request.RequestId);
        }

        if (!LooksLikePdf(source))
        {
            return OptimisationResult.Failure(request.RequestId, "File is not a valid PDF");
        }

        if (AppearsEncrypted(source))
        {
            return OptimisationResult.Failure(request.RequestId, "Ghostscript cannot optimise encrypted PDFs");
        }

        var plan = BuildPlan(request, _options);
        context.ReportProgress(2, plan.AggressiveCompression ? "Optimising PDF (aggressive)" : "Optimising PDF");

        var tempOutput = FilePath.TempFile("clop-pdf", ".pdf", addUniqueSuffix: true);
        tempOutput.EnsureParentDirectoryExists();

        try
        {
            var toolchainResult = await _toolchain.OptimiseAsync(plan, tempOutput, context, cancellationToken).ConfigureAwait(false);
            if (!toolchainResult.Success)
            {
                return OptimisationResult.Failure(request.RequestId, toolchainResult.ErrorMessage ?? "PDF optimisation failed");
            }

            if (!File.Exists(tempOutput.Value))
            {
                return OptimisationResult.Failure(request.RequestId, "Ghostscript produced no output");
            }

            var outputPlan = OptimisedOutputPlanner.Plan(source, "pdf", request.Metadata, BuildCopyOutputPath);
            var finalOutput = outputPlan.Destination;
            finalOutput.EnsureParentDirectoryExists();

            var originalSize = SafeFileSize(source);
            var optimisedSize = SafeFileSize(tempOutput);

            if (plan.RequireSmallerSize && originalSize > 0 && optimisedSize >= originalSize)
            {
                context.ReportProgress(100, "Original already optimal");
                return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, source, "Original already optimal");
            }

            File.Copy(tempOutput.Value, finalOutput.Value, overwrite: true);
            if (plan.PreserveTimestamps)
            {
                CopyTimestamps(source, finalOutput);
            }

            if (outputPlan.RequiresSourceDeletion(source))
            {
                TryDelete(source);
            }

            var message = DescribeImprovement(originalSize, optimisedSize);
            context.ReportProgress(100, message);
            return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, finalOutput, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(OptimiserLog.BuildErrorMessage("PDF optimisation", ex), OptimiserLog.BuildContext(request));
            return OptimisationResult.Failure(request.RequestId, ex.Message);
        }
        finally
        {
            TryDelete(tempOutput);
        }
    }

    private static PdfOptimiserPlan BuildPlan(OptimisationRequest request, PdfOptimiserOptions options)
    {
        var metadata = request.Metadata;
        var aggressive = ReadBool(metadata, OptimisationMetadata.PdfAggressive) ?? options.AggressiveByDefault;
        var stripMetadata = ReadBool(metadata, "pdf.stripMetadata") ?? options.StripMetadata;
        var preserveTimestamps = ReadBool(metadata, "pdf.preserveTimestamps") ?? options.PreserveTimestamps;

        var allowLarger = ReadBool(metadata, "pdf.allowLarger");
        var requireSmaller = ReadBool(metadata, "pdf.requireSmallerSize");
        var requireSizeReduction = requireSmaller ?? (allowLarger.HasValue ? !allowLarger.Value : options.RequireSmallerSize);

        var fontPath = ReadString(metadata, "pdf.fontPath");
        if (string.IsNullOrWhiteSpace(fontPath))
        {
            fontPath = options.FontSearchPath;
        }

        var gsLib = ReadString(metadata, "pdf.gsResourcePath") ?? options.GhostscriptResourceDirectory;

        var insights = PdfDocumentProbe.GetInsights(request.SourcePath, options.ProbeSizeBytes);
        var preset = DeterminePresetProfile(insights, aggressive, options);
        if (TryParsePresetProfile(ReadString(metadata, "pdf.preset"), out var overridePreset))
        {
            preset = overridePreset;
        }

        var linearise = ReadBool(metadata, "pdf.linearize") ?? options.EnableLinearisation;

        return new PdfOptimiserPlan(
            request.SourcePath,
            aggressive,
            stripMetadata,
            preserveTimestamps,
            requireSizeReduction,
            fontPath!,
            gsLib,
            preset,
            insights,
            linearise);
    }

    private static FilePath BuildOutputPath(FilePath source) => BuildCopyOutputPath(source, "pdf");

    internal static FilePath BuildCopyOutputPath(FilePath source, string extension)
    {
        var stem = source.Stem;
        if (!stem.EndsWith(".clop", StringComparison.OrdinalIgnoreCase))
        {
            stem += ".clop";
        }

        var finalExtension = string.IsNullOrWhiteSpace(extension) ? "pdf" : extension;
        var fileName = $"{stem}.{finalExtension}";
        return source.Parent.Append(fileName);
    }

    private static PdfPresetProfile DeterminePresetProfile(PdfDocumentInsights insights, bool aggressive, PdfOptimiserOptions options)
    {
        if (!insights.HasData)
        {
            return aggressive ? PdfPresetProfile.Graphics : PdfPresetProfile.Mixed;
        }

        var looksGraphicsHeavy = insights.EstimatedMaxImageDpi >= options.HighImageDpiThreshold
            || insights.ImageDensity >= options.ImageDensityThreshold
            || insights.MaxImagePixels >= 3200;

        if (looksGraphicsHeavy)
        {
            return PdfPresetProfile.Graphics;
        }

        var looksTextHeavy = insights.PageCount >= options.LongDocumentPageThreshold && insights.ImageDensity < 0.4d;
        if (looksTextHeavy)
        {
            return PdfPresetProfile.Text;
        }

        return aggressive ? PdfPresetProfile.Graphics : PdfPresetProfile.Mixed;
    }

    private static bool TryParsePresetProfile(string? value, out PdfPresetProfile preset)
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, ignoreCase: true, out preset))
        {
            return true;
        }

        preset = PdfPresetProfile.Mixed;
        return false;
    }

    private static bool LooksLikePdf(FilePath path)
    {
        try
        {
            using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 4)
            {
                return false;
            }

            var buffer = new byte[5];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read < 4)
            {
                return false;
            }

            var header = Encoding.ASCII.GetString(buffer, 0, Math.Min(read, buffer.Length));
            return header.StartsWith("%PDF-", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool AppearsEncrypted(FilePath path)
    {
        try
        {
            using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: false);
            var buffer = new char[8192];
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return false;
            }

            var snippet = new string(buffer, 0, read);
            return snippet.IndexOf("/Encrypt", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyTimestamps(FilePath source, FilePath destination)
    {
        try
        {
            var info = new FileInfo(source.Value);
            if (!info.Exists)
            {
                return;
            }

            File.SetCreationTimeUtc(destination.Value, info.CreationTimeUtc);
            File.SetLastWriteTimeUtc(destination.Value, info.LastWriteTimeUtc);
        }
        catch
        {
            // timestamp preservation is best effort only
        }
    }

    private static string DescribeImprovement(long originalSize, long optimisedSize)
    {
        if (optimisedSize <= 0)
        {
            return "Optimised";
        }

        if (originalSize <= 0)
        {
            return "Optimised";
        }

        var diff = originalSize - optimisedSize;
        if (diff <= 0)
        {
            return "Re-encoded";
        }

        return $"Saved {diff.HumanSize()} ({originalSize.HumanSize()} → {optimisedSize.HumanSize()})";
    }

    private static long SafeFileSize(FilePath path)
    {
        try
        {
            return File.Exists(path.Value) ? new FileInfo(path.Value).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDelete(FilePath path)
    {
        try
        {
            if (File.Exists(path.Value))
            {
                File.Delete(path.Value);
            }
        }
        catch
        {
            // ignore cleanup issues
        }
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!TryGetMetadataValue(metadata, key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            _ => null
        };
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!TryGetMetadataValue(metadata, key, out var value))
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => value?.ToString()
        };
    }

    private static bool TryGetMetadataValue(IReadOnlyDictionary<string, object?> metadata, string key, out object? value)
    {
        if (metadata.TryGetValue(key, out value))
        {
            return true;
        }

        if (!key.StartsWith("pdf.", StringComparison.OrdinalIgnoreCase))
        {
            var pdfKey = $"pdf.{key}";
            if (metadata.TryGetValue(pdfKey, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }
}

public sealed record PdfOptimiserPlan(
    FilePath SourcePath,
    bool AggressiveCompression,
    bool StripMetadata,
    bool PreserveTimestamps,
    bool RequireSmallerSize,
    string FontPath,
    string? GhostscriptResourcePath,
    PdfPresetProfile PresetProfile,
    PdfDocumentInsights Insights,
    bool EnableLinearisation)
{
    public bool HasCustomFontPath => !string.IsNullOrWhiteSpace(FontPath);

    public bool HasInsights => Insights.HasData;
}

public interface IPdfToolchain
{
    Task<PdfToolchainResult> OptimiseAsync(PdfOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken);
}

public sealed record PdfToolchainResult(bool Success, string? ErrorMessage = null)
{
    public static PdfToolchainResult Successful() => new(true, null);
    public static PdfToolchainResult Failure(string message) => new(false, message);
}

public enum PdfPresetProfile
{
    Text,
    Mixed,
    Graphics
}

internal sealed class ExternalPdfToolchain : IPdfToolchain
{
    private readonly object _sync = new();
    private PdfOptimiserOptions _options;

    public ExternalPdfToolchain(PdfOptimiserOptions options)
    {
        _options = options;
    }

    public Task<PdfToolchainResult> OptimiseAsync(PdfOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        => OptimiseInternalAsync(plan, tempOutput, context, cancellationToken, attempt: 0);

    private async Task<PdfToolchainResult> OptimiseInternalAsync(PdfOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken, int attempt)
    {
        if (attempt > 1)
        {
            return PdfToolchainResult.Failure("Ghostscript executable was not found after refreshing the installation cache.");
        }

        var options = EnsureGhostscriptOptions();
        var executable = options.GhostscriptPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return PdfToolchainResult.Failure("Ghostscript executable not found. Install Ghostscript 10.x or set the CLOP_GS environment variable to the gswin64c.exe path.");
        }

        if (Path.IsPathRooted(executable) && !File.Exists(executable))
        {
            return PdfToolchainResult.Failure($"Ghostscript not found at '{executable}'");
        }

        var effectiveSource = plan.SourcePath;
        FilePath? linearisedSource = null;

        if (plan.EnableLinearisation)
        {
            linearisedSource = await TryLinearizeAsync(plan, cancellationToken).ConfigureAwait(false);
            if (linearisedSource.HasValue)
            {
                effectiveSource = linearisedSource.Value;
            }
        }

        var args = BuildArguments(options, plan, tempOutput, effectiveSource);
        try
        {
            var tracker = new GhostscriptProgressTracker();

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var gsLib = plan.GhostscriptResourcePath ?? options.GhostscriptResourceDirectory;
            if (!string.IsNullOrWhiteSpace(gsLib))
            {
                startInfo.Environment["GS_LIB"] = gsLib;
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var stdoutCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    stdoutCompletion.TrySetResult();
                    return;
                }

                stdout.AppendLine(e.Data);
                tracker.Process(e.Data, context);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    stderrCompletion.TrySetResult();
                    return;
                }

                stderr.AppendLine(e.Data);
            };

            try
            {
                if (!process.Start())
                {
                    return PdfToolchainResult.Failure($"Unable to start Ghostscript at '{executable}'.");
                }
            }
            catch (Win32Exception win32) when (win32.NativeErrorCode == 2)
            {
                lock (_sync)
                {
                    _options = _options.RefreshGhostscript();
                }

                return await OptimiseInternalAsync(plan, tempOutput, context, cancellationToken, attempt + 1).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return PdfToolchainResult.Failure($"Unable to start Ghostscript: {ex.Message}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignored
                }
            });

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutCompletion.Task, stderrCompletion.Task).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = stderr.Length > 0 ? stderr.ToString().Trim() : stdout.ToString().Trim();
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = $"Ghostscript exited with code {process.ExitCode}";
                }
                Log.Error($"Ghostscript failed: {error}");
                return PdfToolchainResult.Failure(error);
            }

            tracker.Complete(context);
            return PdfToolchainResult.Successful();
        }
        finally
        {
            if (linearisedSource.HasValue)
            {
                TryDeleteFile(linearisedSource.Value);
            }
        }
    }

    private IReadOnlyList<string> BuildArguments(PdfOptimiserOptions options, PdfOptimiserPlan plan, FilePath output, FilePath input)
    {
        var args = new List<string>();
        args.AddRange(options.BaseArguments);
        args.AddRange(plan.AggressiveCompression ? options.LossyArguments : options.LosslessArguments);
        args.AddRange(GetPresetArguments(options, plan.PresetProfile));
        args.Add("-sDEVICE=pdfwrite");
        args.Add($"-sFONTPATH={plan.FontPath}");
        args.Add("-o");
        args.Add(output.Value);

        if (plan.StripMetadata)
        {
            args.AddRange(options.MetadataPreArguments);
        }

        var colorProfiles = WindowsColorProfileHelper.GetProfiles(options.UseWindowsColorProfiles);
        if (!string.IsNullOrWhiteSpace(colorProfiles.DefaultRgbProfile))
        {
            var formatted = FormatPathForArgument(colorProfiles.DefaultRgbProfile);
            args.Add($"-sDefaultRGBProfile={formatted}");
            args.Add($"-sOutputICCProfile={formatted}");
        }

        if (!string.IsNullOrWhiteSpace(colorProfiles.DefaultCmykProfile))
        {
            args.Add($"-sDefaultCMYKProfile={FormatPathForArgument(colorProfiles.DefaultCmykProfile)}");
        }

        args.Add(input.Value);

        if (plan.StripMetadata)
        {
            args.AddRange(options.MetadataPostArguments);
        }

        return args;
    }

    private static IEnumerable<string> GetPresetArguments(PdfOptimiserOptions options, PdfPresetProfile preset)
    {
        return preset switch
        {
            PdfPresetProfile.Text => options.TextPresetArguments,
            PdfPresetProfile.Graphics => options.GraphicsPresetArguments,
            _ => options.MixedPresetArguments
        };
    }

    private PdfOptimiserOptions EnsureGhostscriptOptions()
    {
        lock (_sync)
        {
            if (IsExecutableMissing(_options.GhostscriptPath))
            {
                _options = _options.RefreshGhostscript();
            }

            return _options;
        }
    }

    private string? EnsureQpdfPath()
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_options.QpdfPath) || IsExecutableMissing(_options.QpdfPath))
            {
                _options = _options.RefreshGhostscript();
            }

            return _options.QpdfPath;
        }
    }

    private static bool IsExecutableMissing(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return true;
        }

        if (!Path.IsPathRooted(executable))
        {
            return true;
        }

        return !File.Exists(executable);
    }

    private async Task<FilePath?> TryLinearizeAsync(PdfOptimiserPlan plan, CancellationToken cancellationToken)
    {
        var qpdf = EnsureQpdfPath();
        if (string.IsNullOrWhiteSpace(qpdf))
        {
            return null;
        }

        if (Path.IsPathRooted(qpdf) && !File.Exists(qpdf))
        {
            Log.Warning($"qpdf not found at '{qpdf}'");
            return null;
        }

        var output = FilePath.TempFile("clop-qpdf", ".pdf", addUniqueSuffix: true);
        output.EnsureParentDirectoryExists();

        var startInfo = new ProcessStartInfo(qpdf)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--linearize");
        startInfo.ArgumentList.Add("--object-streams=generate");
        startInfo.ArgumentList.Add("--stream-data=compress");
        startInfo.ArgumentList.Add(plan.SourcePath.Value);
        startInfo.ArgumentList.Add(output.Value);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Win32Exception win32) when (win32.NativeErrorCode == 2)
        {
            lock (_sync)
            {
                _options = _options.RefreshGhostscript();
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning($"Unable to start qpdf: {ex.Message}");
            return null;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignored
            }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var message = stderr.Length > 0 ? stderr.ToString().Trim() : stdout.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(message))
            {
                Log.Warning($"qpdf linearisation failed: {message}");
            }
            TryDeleteFile(output);
            return null;
        }

        return output;
    }

    private static void TryDeleteFile(FilePath path)
    {
        try
        {
            if (File.Exists(path.Value))
            {
                File.Delete(path.Value);
            }
        }
        catch
        {
            // ignore cleanup
        }
    }

    private static string FormatPathForArgument(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.IndexOf(' ') >= 0 ? $"\"{path}\"" : path;
    }
}

internal sealed class GhostscriptProgressTracker
{
    private static readonly Regex PageRangeRegex = new(@"Processing pages \d+ through (\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PageRegex = new(@"^Page\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private int? _totalPages;

    public void Process(string line, OptimiserExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var pageRange = PageRangeRegex.Match(line);
        if (pageRange.Success && int.TryParse(pageRange.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) && pages > 0)
        {
            _totalPages = pages;
            context.ReportProgress(5, $"Processing {pages} pages");
            return;
        }

        var pageMatch = PageRegex.Match(line);
        if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current))
        {
            var percent = _totalPages.HasValue && _totalPages.Value > 0
                ? Math.Clamp(current / (double)_totalPages.Value * 100d, 0d, 99d)
                : Math.Min(99d, current);
            context.ReportProgress(percent, $"Page {current}");
        }
    }

    public void Complete(OptimiserExecutionContext context)
    {
        context.ReportProgress(99, "Finalising PDF");
    }
}
