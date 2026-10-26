using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Imaging;
using ClopWindows.Core.Shared;
using ImageMagick;

namespace ClopWindows.Core.Optimizers;

/// <summary>
/// Image optimiser. See <see cref="ImageOptimisationJob"/> for the strategy: lossless recompression first
/// (mozjpeg's jpegtran, oxipng), then perceptually-guarded lossy encodes (mozjpeg, pngquant, optionally
/// WebP/AVIF) with SSIMULACRA2 deciding how far quality may drop.
/// </summary>
public sealed class ImageOptimiser : IOptimiser
{
    private readonly ImageOptimiserOptions _options;

    public ImageOptimiser(ImageOptimiserOptions? options = null)
    {
        _options = options ?? ImageOptimiserOptions.Default;
    }

    public ItemType ItemType => ItemType.Image;

    public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePath = request.SourcePath;
            if (!File.Exists(sourcePath.Value))
            {
                return OptimisationResult.Failure(request.RequestId, $"Source file not found: {sourcePath.Value}");
            }

            var extension = (sourcePath.Extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension) || !_options.SupportedInputFormats.Contains(extension))
            {
                return OptimisationResult.Unsupported(request.RequestId);
            }

            using var job = new ImageOptimisationJob(request, _options, context, cancellationToken);
            return await job.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or ExternalException or InvalidOperationException)
        {
            Log.Error(OptimiserLog.BuildErrorMessage("Image optimisation", ex), OptimiserLog.BuildContext(request));
            return OptimisationResult.Failure(request.RequestId, ex.Message);
        }
    }
}
