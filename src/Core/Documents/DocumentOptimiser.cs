using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Squish.Core.Optimizers;
using Squish.Core.Shared;

namespace Squish.Core.Documents;

/// <summary>
/// Optimiser for ZIP-based documents: Office Open XML (docx, xlsx, pptx and macro variants), OpenDocument (odt, ods, odp)
/// and EPUB. Everything happens in-process: embedded pictures go through <see cref="ImageOptimiser"/> (same perceptual guard
/// as stand-alone images), the XML is minified, spreadsheet bloat is removed and the package is repacked at maximum deflate.
/// See <see cref="DocumentOptimisationJob"/> for the details and the safety rules.
/// </summary>
public sealed class DocumentOptimiser : IOptimiser
{
    private readonly DocumentOptimiserOptions _options;

    public DocumentOptimiser(DocumentOptimiserOptions? options = null)
    {
        _options = options ?? DocumentOptimiserOptions.Default;
    }

    public ItemType ItemType => ItemType.Document;

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

            var extension = source.Extension ?? string.Empty;
            if (!DocumentSniffer.Extensions.Contains(extension))
            {
                return OptimisationResult.Unsupported(request.RequestId);
            }

            using var job = new DocumentOptimisationJob(request, _options, context, cancellationToken);
            return await job.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or ExternalException or InvalidOperationException or XmlException)
        {
            Log.Error(OptimiserLog.BuildErrorMessage("Document optimisation", ex), OptimiserLog.BuildContext(request));
            return OptimisationResult.Failure(request.RequestId, ex.Message);
        }
    }
}
