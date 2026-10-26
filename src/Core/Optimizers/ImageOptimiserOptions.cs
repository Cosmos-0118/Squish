using System;
using System.Collections.Generic;

namespace Squish.Core.Optimizers;

public sealed record ImageOptimiserOptions
{
    private MetadataPolicyOptions _metadataPolicy = MetadataPolicyOptions.Default;

    public IReadOnlySet<string> SupportedInputFormats { get; init; } = new HashSet<string>(
        new[] { "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "heic", "heif", "webp", "avif" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Source formats that are re-encoded as JPEG when the picture is photographic and has no transparency
    /// (mirrors the "Convert to JPEG" setting).
    /// </summary>
    public IReadOnlySet<string> FormatsToConvertToJpeg { get; init; } = new HashSet<string>(
        new[] { "webp", "avif", "heic", "heif", "bmp" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Source formats that are converted to (lossless) PNG instead - TIFF by default.</summary>
    public IReadOnlySet<string> FormatsToConvertToPng { get; init; } = new HashSet<string>(
        new[] { "tif", "tiff" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// JPEG quality used when the perceptual guard is disabled (a fixed-quality encode), and the quality
    /// formats without a search fall back to. With the guard enabled the encoder searches for the lowest
    /// quality that still meets <see cref="PerceptualGuardOptions.MinimumScore"/>.
    /// </summary>
    public int TargetJpegQuality { get; init; } = 82;

    /// <summary>Lowest JPEG/WebP/AVIF quality the perceptual search is allowed to pick.</summary>
    public int MinimumLossyQuality { get; init; } = 40;

    /// <summary>Highest quality the search will try; above this a re-encode is rarely worth it.</summary>
    public int MaximumLossyQuality { get; init; } = 95;

    public bool DownscaleRetina { get; init; }

    public int RetinaLongEdgePixels { get; init; } = 3840;

    public long? MaxInputPixels { get; init; } = 80_000_000;

    public int? MaxDimension { get; init; } = 12000;

    public bool RequireSizeImprovement { get; init; } = true;

    /// <summary>Minimum relative saving (percent) for a result to replace the original.</summary>
    public double MinimumSavingsPercent { get; init; } = 1.0;

    public AdvancedCodecPreferences AdvancedCodecs { get; init; } = new();

    public PerceptualGuardOptions PerceptualGuard { get; init; } = PerceptualGuardOptions.Default;

    public CropSuggestionOptions CropSuggestions { get; init; } = CropSuggestionOptions.Disabled;

    public MetadataPolicyOptions MetadataPolicy
    {
        get => _metadataPolicy;
        init => _metadataPolicy = value ?? MetadataPolicyOptions.Default;
    }

    public bool PreserveMetadata
    {
        get => _metadataPolicy.PreserveMetadata;
        init => _metadataPolicy = _metadataPolicy with { PreserveMetadata = value };
    }

    public static ImageOptimiserOptions Default { get; } = new();
}

/// <summary>
/// Which external encoders may be used and where to find them. Tools that cannot be located are skipped
/// and a built-in (Magick.NET) encoder takes over, so every flag here is safe to leave on.
/// </summary>
public sealed record AdvancedCodecPreferences
{
    /// <summary>No external tools at all - only the built-in encoders. Used by tests and as a safe mode.</summary>
    public static AdvancedCodecPreferences Disabled { get; } = new()
    {
        EnableMozJpeg = false,
        EnableJpegli = false,
        EnableOxipng = false,
        EnablePngquant = false,
        EnableWebp = false,
        EnableAvif = false,
        EnableGifsicle = false
    };

    /// <summary>mozjpeg: trellis-quantised progressive JPEG, ~10-15% smaller than libjpeg-turbo at equal quality.</summary>
    public bool EnableMozJpeg { get; init; } = true;

    public string? MozJpegPath { get; init; }

    public string? JpegtranPath { get; init; }

    /// <summary>
    /// jpegli (libjxl's <c>cjpegli</c>): a second JPEG encoder searched next to mozjpeg; whichever file is smaller at the
    /// perceptual target ships. Usually 5-20% smaller than mozjpeg on photographs, and faster.
    /// </summary>
    public bool EnableJpegli { get; init; } = true;

    public string? JpegliPath { get; init; }

    /// <summary>oxipng: multi-trial lossless PNG recompression (optional Zopfli).</summary>
    public bool EnableOxipng { get; init; } = true;

    public string? OxipngPath { get; init; }

    /// <summary>
    /// pngquant: lossy 256-colour palette conversion, shipped only when it meets the perceptual target and beats the
    /// lossless result. Already-indexed PNGs are re-quantised in aggressive mode only. Turn off to keep PNGs lossless.
    /// </summary>
    public bool EnablePngquant { get; init; } = true;

    public string? PngquantPath { get; init; }

    public bool EnableWebp { get; init; } = true;

    public string? CwebpPath { get; init; }

    public bool EnableAvif { get; init; } = true;

    public string? AvifEncPath { get; init; }

    public bool EnableGifsicle { get; init; } = true;

    public string? GifsiclePath { get; init; }

    /// <summary>
    /// Allow converting JPEG/PNG sources to WebP when that is smaller at the same perceptual quality.
    /// Off by default because not every app, site or chat client accepts WebP.
    /// </summary>
    public bool AllowWebpOutput { get; init; }

    /// <summary>Same as <see cref="AllowWebpOutput"/> for AVIF (smallest, but least compatible).</summary>
    public bool AllowAvifOutput { get; init; }
}

public sealed record PerceptualGuardOptions
{
    public static PerceptualGuardOptions Default { get; } = new();

    /// <summary>
    /// Turns the SSIMULACRA2-driven quality search and verification on. When off, lossy encodes use fixed
    /// qualities (<see cref="ImageOptimiserOptions.TargetJpegQuality"/>) like the very first versions did.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// SSIMULACRA2 score (100 = identical, ~90 visually lossless, ~80 very high, ~70 high quality) that a lossy
    /// result must reach in normal mode. The score is measured against the decoded source, which for downloaded
    /// photos is itself a ~q75-85 JPEG: at 80 those barely moved (about 25% saved), while 74 saves about 37% with no
    /// visible difference at 1:1. Camera originals clear any of these targets with large savings.
    /// </summary>
    public double MinimumScore { get; init; } = 74;

    /// <summary>Score floor used when the request is flagged aggressive.</summary>
    public double AggressiveMinimumScore { get; init; } = 68;

    /// <summary>Reject (keep the original) instead of shipping a result below the floor.</summary>
    public bool RejectWhenBelowThreshold { get; init; } = true;

    public bool RecordMetrics { get; init; } = true;
}

public sealed record CropSuggestionOptions
{
    public static CropSuggestionOptions Disabled { get; } = new();

    public bool Enabled { get; init; }

    public string? OnnxModelPath { get; init; }

    public bool CacheMasks { get; init; } = true;

    public double EdgeSensitivity { get; init; } = 0.6;

    public int MinimumSubjectAreaPixels { get; init; } = 12_000;
}

public sealed record MetadataPolicyOptions
{
    public static MetadataPolicyOptions Default { get; } = new();

    public bool PreserveMetadata { get; init; }

    public bool PreserveColorProfiles { get; init; } = true;

    public bool StripGpsMetadata { get; init; } = true;

    public IReadOnlySet<int> AdditionalExifTagsToStrip { get; init; } = new HashSet<int>();
}
