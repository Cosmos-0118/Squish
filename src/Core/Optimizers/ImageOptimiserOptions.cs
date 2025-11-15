using System;
using System.Collections.Generic;

namespace ClopWindows.Core.Optimizers;

public sealed record ImageOptimiserOptions
{
    private MetadataPolicyOptions _metadataPolicy = MetadataPolicyOptions.Default;

    public IReadOnlySet<string> SupportedInputFormats { get; init; } = new HashSet<string>(
        new[] { "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "heic", "heif", "webp", "avif" },
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> FormatsToConvertToJpeg { get; init; } = new HashSet<string>(
        new[] { "png", "bmp", "tif", "tiff", "gif", "heic", "heif" },
        StringComparer.OrdinalIgnoreCase);

    public int TargetJpegQuality { get; init; } = 82;

    public bool DownscaleRetina { get; init; } = true;

    public int RetinaLongEdgePixels { get; init; } = 3840;

    public bool RequireSizeImprovement { get; init; } = true;

    public AdvancedCodecPreferences AdvancedCodecs { get; init; } = AdvancedCodecPreferences.Disabled;

    public PerceptualGuardOptions PerceptualGuard { get; init; } = PerceptualGuardOptions.Default;

    public CropSuggestionOptions CropSuggestions { get; init; } = CropSuggestionOptions.Disabled;

    public WicFastPathOptions WicFastPath { get; init; } = WicFastPathOptions.Default;

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

public sealed record AdvancedCodecPreferences
{
    public static AdvancedCodecPreferences Disabled { get; } = new() { EnableMozJpeg = false, EnableWebp = false, EnableAvif = false, EnableHeifConvert = false };

    public bool EnableMozJpeg { get; init; } = true;

    public string? MozJpegPath { get; init; }

    public bool EnableWebp { get; init; } = true;

    public string? CwebpPath { get; init; }

    public bool EnableAvif { get; init; } = true;

    public string? AvifEncPath { get; init; }

    public bool EnableHeifConvert { get; init; } = false;

    public string? HeifConvertPath { get; init; }

    public bool PreferAvifForPhotographic { get; init; } = true;

    public bool PreferWebpFallback { get; init; } = true;

    public bool AllowSoftwareFallbacks { get; init; } = true;
}

public sealed record PerceptualGuardOptions
{
    public static PerceptualGuardOptions Default { get; } = new();

    public bool Enabled { get; init; } = true;

    public double Threshold { get; init; } = 0.965;

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

public sealed record WicFastPathOptions
{
    public static WicFastPathOptions Default { get; } = new();

    public bool Enabled { get; init; } = true;

    public double MinimumSavingsPercent { get; init; } = 2d;

    public bool StripMetadata { get; init; }

    public bool SkipLosslessWhenBelowThreshold { get; init; } = true;

    public int? OverrideJpegQuality { get; init; }
}
