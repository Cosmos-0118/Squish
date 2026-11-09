using Squish.Core.Optimizers;

namespace Squish.Core.Documents;

public sealed record DocumentOptimiserOptions
{
    public static DocumentOptimiserOptions Default { get; } = new();

    /// <summary>Settings for the embedded pictures; the format-conversion and WebP/AVIF switches are overridden per document.</summary>
    public ImageOptimiserOptions ImageOptions { get; init; } = ImageOptimiserOptions.Default;

    /// <summary>Scrub author, company and revision details and drop embedded thumbnails (the "Strip metadata" setting).</summary>
    public bool StripMetadata { get; init; } = true;

    /// <summary>Give the written file the source's creation and modification times.</summary>
    public bool PreserveDates { get; init; } = true;

    /// <summary>Minimum relative saving (percent) for the result to replace the original.</summary>
    public double MinimumSavingsPercent { get; init; } = 1.0;

    /// <summary>Re-encode embedded pictures. Turn off to only repack and clean the document structure.</summary>
    public bool OptimiseImages { get; init; } = true;

    /// <summary>Pictures smaller than this are not worth the round trip through the image pipeline.</summary>
    public long MinimumImageBytes { get; init; } = 8 * 1024;

    /// <summary>Aggressive mode downsamples pictures shown at more than ~1.5x this resolution.</summary>
    public int AggressiveTargetDpi { get; init; } = 150;

    /// <summary>Larger inputs are skipped rather than risking the machine's memory and temp space.</summary>
    public long MaxInputBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}
