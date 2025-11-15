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

            var finalOutput = BuildOutputPath(source);
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
            Log.Error($"PDF optimisation failed: {ex.Message}");
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
        var aggressive = ReadBool(metadata, "pdf.aggressive") ?? options.AggressiveByDefault;
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

        return new PdfOptimiserPlan(
            request.SourcePath,
            aggressive,
            stripMetadata,
            preserveTimestamps,
            requireSizeReduction,
            fontPath!,
            gsLib);
    }

    private static FilePath BuildOutputPath(FilePath source)
    {
        var stem = source.Stem;
        if (!stem.EndsWith(".clop", StringComparison.OrdinalIgnoreCase))
        {
            stem += ".clop";
        }
        var fileName = $"{stem}.pdf";
        return source.Parent.Append(fileName);
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
    string? GhostscriptResourcePath)
{
    public bool HasCustomFontPath => !string.IsNullOrWhiteSpace(FontPath);
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

        var args = BuildArguments(options, plan, tempOutput);
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

    private IReadOnlyList<string> BuildArguments(PdfOptimiserOptions options, PdfOptimiserPlan plan, FilePath output)
    {
        var args = new List<string>();
        args.AddRange(options.BaseArguments);
        args.AddRange(plan.AggressiveCompression ? options.LossyArguments : options.LosslessArguments);
        args.Add("-sDEVICE=pdfwrite");
        args.Add($"-sFONTPATH={plan.FontPath}");
        args.Add("-o");
        args.Add(output.Value);

        if (plan.StripMetadata)
        {
            args.AddRange(options.MetadataPreArguments);
        }

        args.Add(plan.SourcePath.Value);

        if (plan.StripMetadata)
        {
            args.AddRange(options.MetadataPostArguments);
        }

        return args;
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
