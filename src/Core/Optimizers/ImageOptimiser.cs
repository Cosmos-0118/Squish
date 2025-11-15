using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ClopWindows.Core.Optimizers;

[SupportedOSPlatform("windows6.1")]
public sealed class ImageOptimiser : IOptimiser
{
    private const int MinJpegFallbackQuality = 48;

    private readonly ImageOptimiserOptions _options;
    private readonly AdvancedCodecRunner _advancedCodecRunner;
    private readonly CropSuggestionService _cropSuggestionService;

    public ImageOptimiser(ImageOptimiserOptions? options = null)
    {
        _options = options ?? ImageOptimiserOptions.Default;
        _advancedCodecRunner = new AdvancedCodecRunner(_options.AdvancedCodecs);
        _cropSuggestionService = new CropSuggestionService(_options.CropSuggestions);
    }

    public ItemType ItemType => ItemType.Image;

    public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await OptimiseInternalAsync(request, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Image optimisation failed: {ex.Message}");
            return OptimisationResult.Failure(request.RequestId, ex.Message);
        }
    }

    private async Task<OptimisationResult> OptimiseInternalAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
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

        context.ReportProgress(5, "Analysing image");
        var started = DateTime.UtcNow;

        await using var stream = File.OpenRead(sourcePath.Value);
        using var originalImage = await Image.LoadAsync<Rgba32>(stream, cancellationToken).ConfigureAwait(false);
        using var processedImage = originalImage.Clone();

        var hasAlpha = HasVisibleAlpha(originalImage);
        var isAnimated = processedImage.Frames.Count > 1;
        var contentProfile = ImageContentAnalyzer.Analyse(originalImage, hasAlpha);
        var saveProfile = DetermineSaveProfile(extension, contentProfile, hasAlpha, isAnimated);

        if (_options.DownscaleRetina && NeedsRetinaDownscale(originalImage))
        {
            var targetSize = CalculateRetinaSize(originalImage);
            context.ReportProgress(25, $"Downscaling to {targetSize.Width}×{targetSize.Height}");
            processedImage.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = targetSize,
                Sampler = KnownResamplers.Lanczos3,
                Compand = true
            }));
        }

        cancellationToken.ThrowIfCancellationRequested();

        ApplyMetadataPolicies(originalImage, processedImage);

        var cropResult = _cropSuggestionService.Generate(sourcePath, processedImage);
        if (cropResult.Crops.Count > 0)
        {
            context.ReportProgress(35, "Cached crop suggestions");
        }

        var tempOutput = FilePath.TempFile("clop-image", saveProfile.Extension, addUniqueSuffix: true);
        await SaveWithEncoderAsync(processedImage, tempOutput, saveProfile, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        FilePath? stagingForCodecs = null;
        AdvancedCodecResult? advancedResult = null;
        if (_advancedCodecRunner.HasAnyCodec)
        {
            stagingForCodecs = await StageForAdvancedCodecsAsync(processedImage, saveProfile, cancellationToken).ConfigureAwait(false);
            if (stagingForCodecs is not null)
            {
                advancedResult = await _advancedCodecRunner.TryEncodeAsync(stagingForCodecs.Value, saveProfile, _options, contentProfile, cancellationToken).ConfigureAwait(false);
            }
        }

        var candidate = advancedResult?.OutputPath ?? tempOutput;
        var candidateMessage = advancedResult?.Message ?? saveProfile.Description;

        var originalSize = SafeFileSize(sourcePath);
        var candidateSize = SafeFileSize(candidate);

        if (_options.RequireSizeImprovement && candidateSize >= originalSize)
        {
            if (string.Equals(saveProfile.Extension, "jpg", StringComparison.OrdinalIgnoreCase))
            {
                var tuned = await TryReduceJpegSizeAsync(processedImage, originalSize, _options.TargetJpegQuality, cancellationToken).ConfigureAwait(false);
                if (tuned is not null)
                {
                    TryDelete(candidate);
                    candidate = tuned.Value.Path;
                    candidateSize = tuned.Value.Size;
                    candidateMessage = "Adjusted JPEG quality";
                }
            }

            if (candidateSize >= originalSize)
            {
                CleanupTempFiles(tempOutput, advancedResult, stagingForCodecs);
                context.ReportProgress(95, "No size improvement; keeping original");
                return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, sourcePath, "Original already optimal", DateTime.UtcNow - started);
            }
        }

        double? ssim = null;
        if (_options.PerceptualGuard.Enabled)
        {
            using var candidateImage = await Image.LoadAsync<Rgba32>(candidate.Value, cancellationToken).ConfigureAwait(false);
            ssim = ImagePerceptualQualityGuard.ComputeStructuralSimilarity(originalImage, candidateImage);
            if (_options.PerceptualGuard.RejectWhenBelowThreshold && ssim < _options.PerceptualGuard.Threshold)
            {
                CleanupTempFiles(tempOutput, advancedResult, stagingForCodecs);
                context.ReportProgress(90, $"Rejected aggressive encode (SSIM {ssim:0.000})");
                return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, sourcePath, $"Rejected encode below SSIM {_options.PerceptualGuard.Threshold:0.000}", DateTime.UtcNow - started);
            }
        }

        var finalExtension = candidate.Extension ?? saveProfile.Extension;
        var outputPath = BuildOutputPath(sourcePath, finalExtension);
        outputPath.EnsureParentDirectoryExists();
        File.Copy(candidate.Value, outputPath.Value, overwrite: true);

        CleanupTempFiles(tempOutput, advancedResult, stagingForCodecs);

        var message = DescribeImprovement(originalSize, candidateSize, candidateMessage, ssim);
        context.ReportProgress(100, message);

        return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, outputPath, message, DateTime.UtcNow - started);
    }

    private ImageSaveProfile DetermineSaveProfile(string sourceExtension, ImageContentProfile contentProfile, bool hasAlpha, bool isAnimated)
    {
        var jpegQuality = Math.Clamp(_options.TargetJpegQuality, 30, 100);

        if (isAnimated || string.Equals(sourceExtension, "gif", StringComparison.OrdinalIgnoreCase))
        {
            return ImageSaveProfile.ForGif("Normalising GIF");
        }

        if (string.Equals(sourceExtension, "jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(sourceExtension, "jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return ImageSaveProfile.ForJpeg("Re-encoding JPEG", jpegQuality);
        }

        if (!hasAlpha && contentProfile.IsPhotographic && _options.FormatsToConvertToJpeg.Contains(sourceExtension))
        {
            return ImageSaveProfile.ForJpeg("Converting to JPEG", jpegQuality);
        }

        if (sourceExtension is "bmp" or "tif" or "tiff")
        {
            return ImageSaveProfile.ForPng("Converting to PNG", PngCompressionLevel.BestCompression);
        }

        return ImageSaveProfile.ForPng("Normalising PNG", PngCompressionLevel.Level6);
    }

    private bool NeedsRetinaDownscale(Image image)
    {
        if (!_options.DownscaleRetina || _options.RetinaLongEdgePixels <= 0)
        {
            return false;
        }

        var longestEdge = Math.Max(image.Width, image.Height);
        return longestEdge > _options.RetinaLongEdgePixels;
    }

    private static bool HasVisibleAlpha(Image<Rgba32> image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x].A < byte.MaxValue)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private Size CalculateRetinaSize(Image image)
    {
        var longestEdge = Math.Max(image.Width, image.Height);
        var scale = _options.RetinaLongEdgePixels / (double)longestEdge;
        var width = Math.Max(1, EvenInt(image.Width * scale));
        var height = Math.Max(1, EvenInt(image.Height * scale));
        return new Size(width, height);
    }

    private void ApplyMetadataPolicies(Image<Rgba32> source, Image<Rgba32> destination)
    {
        var policy = _options.MetadataPolicy;

        if (!policy.PreserveMetadata)
        {
            destination.Metadata.ExifProfile = null;
            destination.Metadata.IptcProfile = null;
            destination.Metadata.XmpProfile = null;
        }
        else
        {
            var exif = CloneExifProfile(source.Metadata.ExifProfile, policy);
            NormaliseOrientation(destination, exif);
            destination.Metadata.ExifProfile = exif;
            destination.Metadata.IptcProfile = source.Metadata.IptcProfile?.DeepClone();
            destination.Metadata.XmpProfile = source.Metadata.XmpProfile?.DeepClone();
        }

        destination.Metadata.IccProfile = policy.PreserveColorProfiles
            ? source.Metadata.IccProfile?.DeepClone()
            : null;
    }

    private static ExifProfile? CloneExifProfile(ExifProfile? profile, MetadataPolicyOptions policy)
    {
        if (profile is null)
        {
            return null;
        }

        var clone = profile.DeepClone();

        if (policy.StripGpsMetadata)
        {
            clone.RemoveValue(ExifTag.GPSLatitude);
            clone.RemoveValue(ExifTag.GPSLatitudeRef);
            clone.RemoveValue(ExifTag.GPSLongitude);
            clone.RemoveValue(ExifTag.GPSLongitudeRef);
            clone.RemoveValue(ExifTag.GPSAltitude);
            clone.RemoveValue(ExifTag.GPSAltitudeRef);
        }

        foreach (var tagId in policy.AdditionalExifTagsToStrip)
        {
            var match = clone.Values.FirstOrDefault(v => (int)v.Tag == tagId);
            if (match is not null)
            {
                clone.RemoveValue(match.Tag);
            }
        }

        return clone;
    }

    private static void NormaliseOrientation(Image<Rgba32> image, ExifProfile? exif)
    {
        if (exif is null)
        {
            return;
        }

        if (!exif.TryGetValue(ExifTag.Orientation, out IExifValue<ushort>? orientationValue))
        {
            return;
        }

        var orientation = orientationValue.Value;
        switch (orientation)
        {
            case 2:
                image.Mutate(ctx => ctx.Flip(FlipMode.Horizontal));
                break;
            case 3:
                image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate180));
                break;
            case 4:
                image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));
                break;
            case 5:
                image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate90).Flip(FlipMode.Horizontal));
                break;
            case 6:
                image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate90));
                break;
            case 7:
                image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate90).Flip(FlipMode.Vertical));
                break;
            case 8:
                image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate270));
                break;
        }

        exif.RemoveValue(ExifTag.Orientation);
        exif.SetValue(ExifTag.Orientation, (ushort)1);
    }

    private async Task SaveWithEncoderAsync(Image<Rgba32> image, FilePath destination, ImageSaveProfile profile, CancellationToken token)
    {
        destination.EnsureParentDirectoryExists();
        await using var output = new FileStream(destination.Value, FileMode.Create, FileAccess.Write, FileShare.None);
        await image.SaveAsync(output, profile.CreateEncoder(), token).ConfigureAwait(false);
    }

    private async Task<FilePath?> StageForAdvancedCodecsAsync(Image<Rgba32> image, ImageSaveProfile saveProfile, CancellationToken token)
    {
        if (!_advancedCodecRunner.HasAnyCodec)
        {
            return null;
        }

        var stageExtension = saveProfile.Extension is "jpg" or "jpeg" ? "jpg" : "png";
        var encoderProfile = stageExtension is "jpg"
            ? ImageSaveProfile.ForJpeg("Advanced codec staging", Math.Clamp(_options.TargetJpegQuality, 60, 95))
            : ImageSaveProfile.ForPng("Advanced codec staging", PngCompressionLevel.BestCompression);

        var stagePath = FilePath.TempFile("clop-stage", stageExtension, addUniqueSuffix: true);
        await SaveWithEncoderAsync(image, stagePath, encoderProfile, token).ConfigureAwait(false);
        return stagePath;
    }

    private async Task<(FilePath Path, long Size)?> TryReduceJpegSizeAsync(Image<Rgba32> image, long maxBytes, int startingQuality, CancellationToken token)
    {
        var high = Math.Clamp(startingQuality, MinJpegFallbackQuality + 1, 100) - 1;
        var low = MinJpegFallbackQuality;

        FilePath? bestCandidate = null;
        var bestSize = long.MaxValue;
        var scratch = new List<FilePath>();

        while (low <= high)
        {
            token.ThrowIfCancellationRequested();

            var quality = (low + high) / 2;
            var candidate = FilePath.TempFile("clop-image-quality", "jpg", addUniqueSuffix: true);
            await SaveWithEncoderAsync(image, candidate, ImageSaveProfile.ForJpeg("quality-tune", quality), token).ConfigureAwait(false);
            var size = SafeFileSize(candidate);

            if (size > 0 && size < maxBytes)
            {
                if (size < bestSize)
                {
                    if (bestCandidate is { } prev)
                    {
                        scratch.Add(prev);
                    }

                    bestCandidate = candidate;
                    bestSize = size;
                }
                else
                {
                    scratch.Add(candidate);
                }

                high = quality - 1;
            }
            else
            {
                scratch.Add(candidate);
                low = quality + 1;
            }
        }

        foreach (var leftover in scratch)
        {
            TryDelete(leftover);
        }

        if (bestCandidate is null)
        {
            return null;
        }

        return (bestCandidate.Value, bestSize);
    }

    private static string DescribeImprovement(long originalSize, long optimisedSize, string profileMessage, double? ssim)
    {
        var message = profileMessage;

        if (originalSize > 0 && optimisedSize > 0)
        {
            var diff = originalSize - optimisedSize;
            message = diff > 0
                ? $"{profileMessage}. Saved {diff.HumanSize()} ({originalSize.HumanSize()} → {optimisedSize.HumanSize()})"
                : $"{profileMessage}. Re-encoded";
        }

        if (ssim is double value)
        {
            message += $" (SSIM {value:0.000})";
        }

        return message;
    }

    private static void CleanupTempFiles(FilePath tempOutput, AdvancedCodecResult? advanced, FilePath? staging)
    {
        TryDelete(tempOutput);

        if (advanced?.OutputPath is { } advancedPath)
        {
            TryDelete(advancedPath);
        }

        if (staging is { } stage)
        {
            TryDelete(stage);
        }
    }

    private static FilePath BuildOutputPath(FilePath sourcePath, string extension)
    {
        var stem = sourcePath.Stem;
        if (stem.EndsWith(".clop", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^5];
        }

        var fileName = $"{stem}.clop.{extension}";
        return sourcePath.Parent.Append(fileName);
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

    private static int EvenInt(double value)
    {
        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return (rounded / 2) * 2;
    }
}
