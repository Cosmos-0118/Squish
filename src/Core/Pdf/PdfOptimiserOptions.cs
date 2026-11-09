using Squish.Core.Optimizers;

namespace Squish.Core.Pdf;

public sealed record PdfOptimiserOptions
{
    public static PdfOptimiserOptions Default { get; } = new();

    /// <summary>Settings for re-encoding embedded pictures (perceptual guard, JPEG quality limits, encoders).</summary>
    public ImageOptimiserOptions ImageOptions { get; init; } = ImageOptimiserOptions.Default;

    /// <summary>Drop the document information dictionary, XMP metadata, page thumbnails and PieceInfo (the "Strip metadata" setting).</summary>
    public bool StripMetadata { get; init; } = true;

    public bool PreserveDates { get; init; } = true;

    /// <summary>Minimum relative saving (percent) for the result to replace the original.</summary>
    public double MinimumSavingsPercent { get; init; } = 1.0;

    /// <summary>Recompress embedded pictures. Off leaves only the lossless structural pass.</summary>
    public bool OptimiseImages { get; init; } = true;

    /// <summary>Pictures drawn at more than this resolution are downsampled to it (normal mode).</summary>
    public int TargetDpi { get; init; } = 220;

    /// <summary>The same for aggressive mode.</summary>
    public int AggressiveTargetDpi { get; init; } = 150;

    /// <summary>A resample must remove at least this fraction of the linear resolution, otherwise it only costs quality.</summary>
    public double MinimumDownscaleFactor { get; init; } = 0.85;

    public long MinimumImageBytes { get; init; } = 8 * 1024;

    public long MaxInputBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Explicit qpdf location; otherwise the bundled tool is used.</summary>
    public string? QpdfPath { get; init; }
}
