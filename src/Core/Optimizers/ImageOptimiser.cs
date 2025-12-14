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
    private const int AggressiveJpegQuality = 68;
    private const int MaxJpegQualityTuningIterations = 8;

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
            Log.Error(OptimiserLog.BuildErrorMessage("Image optimisation", ex), OptimiserLog.BuildContext(request));
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

        var guard = ImageInputGuards.Validate(sourcePath, _options, cancellationToken);
        if (!guard.IsValid)
        {
            return OptimisationResult.Failure(request.RequestId, guard.Error ?? "Input image exceeds configured limits");
        }

        var targetJpegQuality = ResolveTargetJpegQuality(request.Metadata);

        var fastPathResult = await TryFastPathAsync(request, extension, targetJpegQuality, context, cancellationToken).ConfigureAwait(false);
        if (fastPathResult is not null)
        {
            return fastPathResult;
        }

        context.ReportProgress(5, "Analysing image");
        var started = DateTime.UtcNow;

        using var originalImage = await Image.LoadAsync<Rgba32>(sourcePath.Value, cancellationToken).ConfigureAwait(false);
        using var processedImage = originalImage.Clone();

        var hasAlpha = HasVisibleAlpha(originalImage);
        var isAnimated = processedImage.Frames.Count > 1;
        var contentProfile = ImageContentAnalyzer.Analyse(originalImage, hasAlpha);
        var saveProfile = DetermineSaveProfile(extension, contentProfile, hasAlpha, isAnimated, targetJpegQuality);

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
            stagingForCodecs = await StageForAdvancedCodecsAsync(processedImage, saveProfile, targetJpegQuality, cancellationToken).ConfigureAwait(false);
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
                var tuned = await TryReduceJpegSizeAsync(processedImage, originalSize, targetJpegQuality, cancellationToken).ConfigureAwait(false);
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
        var outputPlan = OptimisedOutputPlanner.Plan(sourcePath, finalExtension, request.Metadata, BuildOutputPath);
        var outputPath = outputPlan.Destination;
        outputPath.EnsureParentDirectoryExists();
        File.Copy(candidate.Value, outputPath.Value, overwrite: true);

        CleanupTempFiles(tempOutput, advancedResult, stagingForCodecs);

        if (outputPlan.RequiresSourceDeletion(sourcePath))
        {
            TryDelete(sourcePath);
        }

        var message = DescribeImprovement(originalSize, candidateSize, candidateMessage, ssim);
        context.ReportProgress(100, message);

        return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, outputPath, message, DateTime.UtcNow - started);
    }

    private async Task<OptimisationResult?> TryFastPathAsync(OptimisationRequest request, string extension, int targetJpegQuality, OptimiserExecutionContext context, CancellationToken token)
    {
        if (OptimisationMetadata.GetFlag(request.Metadata, OptimisationMetadata.ImageForceFullOptimisation))
        {
            return null;
        }

        if (!ImageFastPathPolicies.TryBuildEffectiveOptions(_options, out var fastPathOptions))
        {
            return null;
        }

        if (!WicImageTranscoder.IsSupported(extension))
        {
            return null;
        }

        if (ShouldSkipFastPath(request.SourcePath, extension))
        {
            return null;
        }

        var outcome = await WicImageTranscoder.TryTranscodeAsync(request.SourcePath, extension, fastPathOptions, targetJpegQuality, token).ConfigureAwait(false);
        if (outcome is null)
        {
            return null;
        }

        if (outcome.Status == WicTranscodeStatus.Success
            && outcome.OutputPath is { } fastPathOutput
            && ImageFastPathPolicies.RequiresPerceptualValidation(_options))
        {
            var guardResult = await ImageFastPathPerceptualValidator.ValidateAsync(request, fastPathOutput, _options.PerceptualGuard, token).ConfigureAwait(false);
            if (guardResult is not null)
            {
                TryDelete(fastPathOutput);
                return guardResult;
            }
        }

        return outcome.Status switch
        {
            WicTranscodeStatus.Success => CompleteFastPathSuccess(request, context, outcome),
            WicTranscodeStatus.NoSavings => new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, request.SourcePath, "Original already optimal (fast path)"),
            _ => null
        };
    }

    private bool ShouldSkipFastPath(FilePath sourcePath, string extension)
    {
        var imageInfo = IdentifyImageInfo(sourcePath);

        if (NeedsRetinaDownscale(imageInfo))
        {
            return true;
        }

        if (ShouldPreferFormatConversion(extension, imageInfo))
        {
            return true;
        }

        return false;
    }

    private OptimisationResult CompleteFastPathSuccess(OptimisationRequest request, OptimiserExecutionContext context, WicTranscodeOutcome outcome)
    {
        if (outcome.OutputPath is null)
        {
            return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, request.SourcePath, "Original already optimal (fast path)");
        }

        var finalExtension = string.IsNullOrWhiteSpace(outcome.Extension)
            ? request.SourcePath.Extension ?? "png"
            : outcome.Extension;

        var outputPlan = OptimisedOutputPlanner.Plan(request.SourcePath, finalExtension, request.Metadata, BuildOutputPath);
        var finalOutput = outputPlan.Destination;
        finalOutput.EnsureParentDirectoryExists();

        var fastPathOutput = outcome.OutputPath.Value;
        File.Copy(fastPathOutput.Value, finalOutput.Value, overwrite: true);
        TryDelete(fastPathOutput);

        if (outputPlan.RequiresSourceDeletion(request.SourcePath))
        {
            TryDelete(request.SourcePath);
        }

        var message = $"Fast path saved {outcome.SavingsPercent:0.0}% ({outcome.OriginalBytes.HumanSize()} → {outcome.OutputBytes.HumanSize()})";
        context.ReportProgress(95, message);
        return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, finalOutput, message);
    }

    private ImageSaveProfile DetermineSaveProfile(string sourceExtension, ImageContentProfile contentProfile, bool hasAlpha, bool isAnimated, int targetJpegQuality)
    {
        var jpegQuality = Math.Clamp(targetJpegQuality, 30, 100);

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

    private bool NeedsRetinaDownscale(ImageInfo? info)
    {
        if (!_options.DownscaleRetina || _options.RetinaLongEdgePixels <= 0 || info is null)
        {
            return false;
        }

        var longestEdge = Math.Max(info.Width, info.Height);
        return longestEdge > _options.RetinaLongEdgePixels;
    }

    private bool ShouldPreferFormatConversion(string extension, ImageInfo? info)
    {
        if (info is null)
        {
            return false;
        }

        if (!_options.FormatsToConvertToJpeg.Contains(extension))
        {
            return false;
        }

        if (HasAlphaChannel(info))
        {
            return false;
        }

        const long PhotographicPixelThreshold = 1_000_000;
        var pixelCount = (long)info.Width * info.Height;
        return pixelCount >= PhotographicPixelThreshold;
    }

    private static bool HasAlphaChannel(ImageInfo info)
    {
        var alphaRepresentation = info.PixelType.AlphaRepresentation;
        return alphaRepresentation is PixelAlphaRepresentation.Associated or PixelAlphaRepresentation.Unassociated;
    }

    private static ImageInfo? IdentifyImageInfo(FilePath sourcePath)
    {
        try
        {
            return Image.Identify(sourcePath.Value) as ImageInfo;
        }
        catch
        {
            return null;
        }
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

    private async Task<FilePath?> StageForAdvancedCodecsAsync(Image<Rgba32> image, ImageSaveProfile saveProfile, int targetJpegQuality, CancellationToken token)
    {
        if (!_advancedCodecRunner.HasAnyCodec)
        {
            return null;
        }

        var stageExtension = saveProfile.Extension is "jpg" or "jpeg" ? "jpg" : "png";
        var encoderProfile = stageExtension is "jpg"
            ? ImageSaveProfile.ForJpeg("Advanced codec staging", Math.Clamp(targetJpegQuality, 60, 95))
            : ImageSaveProfile.ForPng("Advanced codec staging", PngCompressionLevel.BestCompression);

        var stagePath = FilePath.TempFile("clop-stage", stageExtension, addUniqueSuffix: true);
        await SaveWithEncoderAsync(image, stagePath, encoderProfile, token).ConfigureAwait(false);
        return stagePath;
    }

    private int ResolveTargetJpegQuality(IReadOnlyDictionary<string, object?> metadata)
    {
        var quality = _options.TargetJpegQuality;
        if (OptimisationMetadata.GetFlag(metadata, OptimisationMetadata.ImageAggressive))
        {
            quality = Math.Min(quality, AggressiveJpegQuality);
        }

        return quality;
    }

    private static ImageSaveProfile BuildQualityTuningProfile(int quality)
    {
        return ImageSaveProfile.ForJpeg("quality-tune", quality);
    }

    private static void ResetScratchStream(MemoryStream stream)
    {
        stream.Position = 0;
        stream.SetLength(0);
    }

    private static async Task<long> MeasureJpegSizeAsync(Image<Rgba32> image, int quality, MemoryStream scratch, CancellationToken token)
    {
        ResetScratchStream(scratch);
        await image.SaveAsync(scratch, BuildQualityTuningProfile(quality).CreateEncoder(), token).ConfigureAwait(false);
        return scratch.Length;
    }

    private async Task<(int Quality, long Size)?> FindJpegQualityUnderAsync(Image<Rgba32> image, long maxBytes, int startingQuality, CancellationToken token)
    {
        var high = Math.Clamp(startingQuality, MinJpegFallbackQuality + 1, 100) - 1;
        var low = MinJpegFallbackQuality;
        var iterationsRemaining = MaxJpegQualityTuningIterations;

        (int Quality, long Size)? best = null;

        using var scratch = new MemoryStream(capacity: 256 * 1024);

        while (low <= high && iterationsRemaining-- > 0)
        {
            token.ThrowIfCancellationRequested();

            var quality = (low + high) / 2;
            var size = await MeasureJpegSizeAsync(image, quality, scratch, token).ConfigureAwait(false);

            if (size > 0 && size < maxBytes)
            {
                if (best is null || size < best.Value.Size)
                {
                    best = (quality, size);
                }
                high = quality - 1;
            }
            else
            {
                low = quality + 1;
            }
        }

        return best;
    }

    private async Task<(FilePath Path, long Size)?> TryReduceJpegSizeAsync(Image<Rgba32> image, long maxBytes, int startingQuality, CancellationToken token)
    {
        var result = await FindJpegQualityUnderAsync(image, maxBytes, startingQuality, token).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        var output = FilePath.TempFile("clop-image-quality", "jpg", addUniqueSuffix: true);
        await SaveWithEncoderAsync(image, output, BuildQualityTuningProfile(result.Value.Quality), token).ConfigureAwait(false);

        var finalSize = SafeFileSize(output);
        if (finalSize <= 0)
        {
            TryDelete(output);
            return null;
        }

        return (output, finalSize);
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
