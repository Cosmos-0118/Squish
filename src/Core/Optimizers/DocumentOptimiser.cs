using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public sealed class DocumentOptimiser : IOptimiser
{
    private readonly DocumentConversionOptions _options;
    private readonly IDocumentConverter _converter;
    private readonly PdfOptimiser _pdfOptimiser;

    public DocumentOptimiser(DocumentConversionOptions? options = null, IDocumentConverter? converter = null, PdfOptimiser? pdfOptimiser = null)
    {
        _options = options ?? DocumentConversionOptions.Default;
        _converter = converter ?? new LibreOfficeDocumentConverter();
        _pdfOptimiser = pdfOptimiser ?? new PdfOptimiser();
    }

    public ItemType ItemType => ItemType.Document;

    public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var enabled = _options.EnabledEvaluator?.Invoke() ?? SettingsHost.Get(SettingsRegistry.AutoConvertDocumentsToPdf);
        if (!enabled)
        {
            return OptimisationResult.Unsupported(request.RequestId);
        }

        var source = request.SourcePath;
        if (!File.Exists(source.Value))
        {
            return OptimisationResult.Failure(request.RequestId, $"Source file not found: {source.Value}");
        }

        if (!MediaFormats.IsDocument(source))
        {
            return OptimisationResult.Unsupported(request.RequestId);
        }

        if (!_converter.Supports(source, _options))
        {
            return OptimisationResult.Failure(request.RequestId, $"Unsupported document format '{source.Extension}'.");
        }

        context.ReportProgress(2, "Converting to PDF");
        var workspace = CreateWorkspace();
        DocumentConversionResult conversionResult;

        try
        {
            conversionResult = await _converter.ConvertToPdfAsync(source, workspace, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupWorkspace(workspace);
            throw;
        }
        catch (Exception ex)
        {
            CleanupWorkspace(workspace);
            return OptimisationResult.Failure(request.RequestId, ex.Message);
        }

        if (!conversionResult.Success || conversionResult.OutputPath is null)
        {
            CleanupWorkspace(workspace);
            return OptimisationResult.Failure(request.RequestId, conversionResult.ErrorMessage ?? "Document conversion failed.");
        }

        context.ReportProgress(40, "Optimising converted PDF");
        var convertedPdf = conversionResult.OutputPath.Value;

        var pdfRequest = new OptimisationRequest(ItemType.Pdf, convertedPdf, request.RequestId, request.Metadata);
        var pdfResult = await _pdfOptimiser.OptimiseAsync(pdfRequest, context, cancellationToken).ConfigureAwait(false);

        if (pdfResult.Status != OptimisationStatus.Succeeded)
        {
            CleanupWorkspace(workspace);
            return pdfResult;
        }

        var plannedOutput = OptimisedOutputPlanner.Plan(request.SourcePath, "pdf", request.Metadata, PdfOptimiser.BuildCopyOutputPath);
        var pdfOutput = pdfResult.OutputPath ?? convertedPdf;
        var finalOutput = plannedOutput.Destination;
        finalOutput.EnsureParentDirectoryExists();

        File.Copy(pdfOutput.Value, finalOutput.Value, overwrite: true);
        if (_options.PreserveSourceTimestamps)
        {
            CopyTimestamps(source, finalOutput);
        }

        if (plannedOutput.RequiresSourceDeletion(source))
        {
            TryDelete(source);
        }

        if (!string.Equals(pdfOutput.Value, finalOutput.Value, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(pdfOutput);
        }

        CleanupWorkspace(workspace);
        var message = string.IsNullOrWhiteSpace(pdfResult.Message)
            ? "Converted to PDF"
            : $"Converted to PDF · {pdfResult.Message}";

        context.ReportProgress(100, message);
        return pdfResult with { OutputPath = finalOutput, Message = message };
    }

    private static FilePath CreateWorkspace()
    {
        var root = ClopPaths.Conversions.Append("documents");
        root.EnsurePathExists();
        var unique = root.Append($"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{NanoId.New(6)}");
        Directory.CreateDirectory(unique.Value);
        return unique;
    }

    private static void CleanupWorkspace(FilePath workspace)
    {
        try
        {
            if (Directory.Exists(workspace.Value))
            {
                Directory.Delete(workspace.Value, recursive: true);
            }
        }
        catch
        {
            // best effort
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
            // ignored
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
            // ignored
        }
    }
}
