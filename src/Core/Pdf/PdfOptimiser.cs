using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Optimizers;
using Squish.Core.Shared;

namespace Squish.Core.Pdf;

/// <summary>
/// PDF optimiser built on qpdf (Apache-2.0, bundled as <c>qpdf.exe</c> + <c>qpdf30.dll</c>):
/// <list type="number">
/// <item>structure - maximum deflate, objects packed into object streams, unreferenced objects collected, optional removal of
/// the information dictionary / XMP / thumbnails;</item>
/// <item>pictures - JPEG and bitmap re-encoding under the same perceptual guard as stand-alone images, with downsampling of
/// pictures drawn at a much higher resolution than needed (see <see cref="PdfImagePass"/>).</item>
/// Both land in a single qpdf rewrite of the source.
/// </list>
/// Text, fonts, forms, annotations and bookmarks are never rewritten. Signed and encrypted files are left alone.
/// </summary>
public sealed class PdfOptimiser : IOptimiser
{
    private readonly PdfOptimiserOptions _options;

    public PdfOptimiser(PdfOptimiserOptions? options = null)
    {
        _options = options ?? PdfOptimiserOptions.Default;
    }

    public ItemType ItemType => ItemType.Pdf;

    public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = request.SourcePath;
            if (!File.Exists(source.Value))
            {
                return OptimisationResult.Failure(request.RequestId, $"Source file not found: {source.Value}");
            }

            if (!string.Equals(source.Extension, "pdf", StringComparison.OrdinalIgnoreCase))
            {
                return OptimisationResult.Unsupported(request.RequestId);
            }

            return await new PdfOptimisationJob(request, _options, context, cancellationToken).RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or ExternalException or InvalidOperationException)
        {
            Log.Error(OptimiserLog.BuildErrorMessage("PDF optimisation", ex), OptimiserLog.BuildContext(request));
            return OptimisationResult.Failure(request.RequestId, ex.Message);
        }
    }
}

internal sealed class PdfOptimisationJob
{
    private readonly OptimisationRequest _request;
    private readonly PdfOptimiserOptions _options;
    private readonly OptimiserExecutionContext _context;
    private readonly CancellationToken _token;
    private readonly FilePath _source;
    private readonly bool _aggressive;

    public PdfOptimisationJob(OptimisationRequest request, PdfOptimiserOptions options, OptimiserExecutionContext context, CancellationToken token)
    {
        _request = request;
        _options = options;
        _context = context;
        _token = token;
        _source = request.SourcePath;
        _aggressive = OptimisationMetadata.GetFlag(request.Metadata, OptimisationMetadata.PdfAggressive);
    }

    public async Task<OptimisationResult> RunAsync()
    {
        var started = DateTime.UtcNow;
        _context.ReportProgress(3, "Inspecting PDF");

        var originalSize = new FileInfo(_source.Value).Length;
        if (originalSize > _options.MaxInputBytes)
        {
            return Untouched(started, "PDF is too large to optimise safely");
        }

        var head = await ReadHeadAsync().ConfigureAwait(false);
        if (!head.Contains("%PDF-", StringComparison.Ordinal))
        {
            return Untouched(started, "Not a PDF file; left untouched");
        }

        var qpdfPath = QpdfRunner.Resolve(_options.QpdfPath);
        if (qpdfPath is null)
        {
            return OptimisationResult.Failure(_request.RequestId, "PDF optimisation needs qpdf, which was not found (tools/qpdf).");
        }

        if (await ContainsSignatureAsync().ConfigureAwait(false))
        {
            return Untouched(started, "Digitally signed PDF; left untouched");
        }

        var qpdf = new QpdfRunner(qpdfPath);
        var encrypted = await qpdf.RunAsync(["--is-encrypted", _source.Value], _token).ConfigureAwait(false);
        if (encrypted.ExitCode == 0)
        {
            return Untouched(started, "Encrypted PDF; left untouched");
        }

        var workDirectory = TempRuns.CreateDirectory();
        try
        {
            return await OptimiseCoreAsync(qpdf, head, originalSize, workDirectory, started).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private async Task<OptimisationResult> OptimiseCoreAsync(QpdfRunner qpdf, string head, long originalSize, string workDirectory, DateTime started)
    {
        var linearize = head.Contains("/Linearized", StringComparison.Ordinal);

        // 1. Read the structure once. Everything below works from the source file directly: a separate "structural" rewrite
        // first would deflate every stream at maximum effort only for the picture pass to unpack and deflate it all again.
        _context.ReportProgress(10, "Reading PDF structure");
        var structurePath = Path.Combine(workDirectory, "structure.json");
        var export = await qpdf.RunAsync([_source.Value, structurePath, "--json-output", "--json-stream-data=none"], _token).ConfigureAwait(false);
        if (!QpdfRunner.Succeeded(export) || !File.Exists(structurePath))
        {
            Log.Warning($"qpdf could not read {_source.Value}: {export.StandardError}");
            return Untouched(started, "Could not read this PDF; left untouched");
        }

        using var structure = PdfObjectMap.Load(structurePath);
        var predictors = structure.HasPredictorStreams();
        var shared = new List<string>();
        if (_options.StripMetadata)
        {
            shared.Add("--remove-info");
            shared.Add("--remove-metadata");
        }

        string? best = null;
        var notes = new List<string> { "recompressed structure" };

        // 2. Pictures (and the metadata that lives in object dictionaries), applied in the one rewrite.
        if (_options.OptimiseImages)
        {
            _context.ReportProgress(25, "Optimising pictures");
            var pass = new PdfImagePass(qpdf, structure, _options, _aggressive, workDirectory, _context, _token);
            var result = await pass.RunAsync(_source.Value, Path.Combine(workDirectory, "images"), linearize, shared, predictors).ConfigureAwait(false);
            if (result.Output is { } output)
            {
                best = output;
                if (result.ImagesChanged > 0)
                {
                    notes.Add($"optimised {result.ImagesChanged} picture{(result.ImagesChanged == 1 ? string.Empty : "s")}");
                }
            }
        }

        // Nothing to change in the objects (or pictures are off): only the structure is rewritten.
        if (best is null)
        {
            _context.ReportProgress(60, "Recompressing PDF structure");
            best = await qpdf.WriteBestAsync(_source.Value, Path.Combine(workDirectory, "structural"), shared, linearize, predictors, _token).ConfigureAwait(false);
            if (best is null)
            {
                return Untouched(started, "Could not read this PDF; left untouched");
            }
        }

        // 3. Verify, then decide whether it is worth replacing the original.
        _context.ReportProgress(90, "Checking result");
        var check = await qpdf.RunAsync(["--check", best], _token).ConfigureAwait(false);
        if (!QpdfRunner.Succeeded(check))
        {
            Log.Warning($"qpdf --check rejected the optimised {_source.Value}: {check.StandardOutput} {check.StandardError}");
            return Untouched(started, "Could not rewrite safely; original kept");
        }

        var newSize = new FileInfo(best).Length;
        var minimumSaving = originalSize * (_options.MinimumSavingsPercent / 100.0);
        if (newSize >= originalSize - minimumSaving)
        {
            _context.ReportProgress(95, "No size improvement; keeping original");
            return Untouched(started, "Original already optimal");
        }

        var plan = OptimisedOutputPlanner.Plan(_source, "pdf", _request.Metadata, BuildOutputPath);
        var destination = plan.Destination;
        destination.EnsureParentDirectoryExists();

        var times = _options.PreserveDates ? ReadTimestamps(_source) : null;
        SafeFileWriter.CopyOver(best, destination.Value);
        if (times is { } t)
        {
            RestoreTimestamps(destination, t);
        }

        var message = $"{string.Join(", ", notes)}. Saved {(originalSize - newSize).HumanSize()} ({originalSize.HumanSize()} → {newSize.HumanSize()})";
        _context.ReportProgress(100, message);
        return new OptimisationResult(_request.RequestId, OptimisationStatus.Succeeded, destination, message, DateTime.UtcNow - started);
    }

    private OptimisationResult Untouched(DateTime started, string reason)
    {
        _context.ReportProgress(100, reason);
        return new OptimisationResult(_request.RequestId, OptimisationStatus.Succeeded, _source, reason, DateTime.UtcNow - started);
    }

    private async Task<string> ReadHeadAsync()
    {
        var buffer = new byte[1024];
        await using var stream = new FileStream(_source.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = await stream.ReadAsync(buffer, _token).ConfigureAwait(false);
        return Encoding.Latin1.GetString(buffer, 0, read);
    }

    /// <summary>A digital signature covers exact byte ranges, so any rewrite would void it.</summary>
    private async Task<bool> ContainsSignatureAsync()
    {
        var needle = "/ByteRange"u8.ToArray();
        var buffer = new byte[1024 * 1024 + needle.Length];
        var carried = 0;
        await using var stream = new FileStream(_source.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan | FileOptions.Asynchronous);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(carried, buffer.Length - carried), _token).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            var total = carried + read;
            if (buffer.AsSpan(0, total).IndexOf(needle) >= 0)
            {
                return true;
            }

            carried = Math.Min(needle.Length - 1, total);
            buffer.AsSpan(total - carried, carried).CopyTo(buffer);
        }
    }

    private static FilePath BuildOutputPath(FilePath sourcePath, string extension)
    {
        var stem = sourcePath.Stem;
        const string suffix = ".squish";
        if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^suffix.Length];
        }

        return sourcePath.Parent.Append($"{stem}.squish.{extension}");
    }

    private static (DateTime Created, DateTime Modified)? ReadTimestamps(FilePath path)
    {
        try
        {
            return (File.GetCreationTimeUtc(path.Value), File.GetLastWriteTimeUtc(path.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RestoreTimestamps(FilePath path, (DateTime Created, DateTime Modified) times)
    {
        try
        {
            File.SetCreationTimeUtc(path.Value, times.Created);
            File.SetLastWriteTimeUtc(path.Value, times.Modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Timestamps are a nicety; they must not fail a finished optimisation.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; the OS temp cleaner gets the rest
        }
    }
}
