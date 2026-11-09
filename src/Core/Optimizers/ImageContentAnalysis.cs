using System;
using System.Collections.Generic;
using Squish.Core.Imaging;

namespace Squish.Core.Optimizers;

internal enum ImageContentKind
{
    Photograph,
    Graphic,
    Document
}

internal readonly record struct ImageContentProfile(
    ImageContentKind Kind,
    double EdgeDensity,
    double UniqueColorRatio,
    double BrightnessVariance,
    double WhitespaceRatio,
    bool HasAlpha)
{
    public bool IsPhotographic => Kind == ImageContentKind.Photograph;

    /// <summary>Share of sampled pixels covered by the 48 most common exact 24-bit colours.</summary>
    public double TopColorCoverage { get; init; }

    /// <summary>Same, after dropping the low 3 bits of every channel (see <see cref="ImageContentAnalyzer"/>).</summary>
    public double QuantizedTopColorCoverage { get; init; }
}

/// <summary>
/// Cheap content classifier (photo vs. flat graphic vs. text document) driving codec and chroma-subsampling
/// choices. It samples at most ~256x256 points so it stays negligible next to the encode itself.
/// </summary>
/// <remarks>
/// "A handful of colours cover nearly every pixel" is judged twice: on exact 24-bit colours and on colours quantised to
/// 5 bits per channel. Real screenshots rarely have a handful of <em>exact</em> colours: window tints carry a dither
/// or noise texture, shadows are smooth ramps and sub-pixel text anti-aliasing adds a long tail of near-identical
/// shades, so the exact test alone sends a flat UI capture down the photograph path (JPEG candidates, no lossless
/// WebP, 4:2:0). Merging shades that differ by less than 8 levels recovers those, while a photograph spreads over
/// far more than 48 of the 32768 buckets.
/// </remarks>
internal static class ImageContentAnalyzer
{
    private const double DocumentWhitespaceThreshold = 0.52;
    private const double GraphicUniqueColorThreshold = 0.18;
    private const double GraphicEdgeThreshold = 0.12;
    private const double GraphicTopColorCoverage = 0.90;

    // Merging shades makes coverage easier to reach, so the quantised test asks for more - and, to keep smooth low-contrast
    // photographs (a noisy sky gradient reaches ~0.88) out, only counts when the picture has hard edges: text, panels, lines.
    private const double GraphicQuantizedTopColorCoverage = 0.93;
    private const double GraphicQuantizedMinEdgeDensity = 0.02;
    private const int TopColorCount = 48;
    private const int QuantizeShift = 3;
    private const int QuantizedBuckets = 1 << (3 * (8 - QuantizeShift));

    public static ImageContentProfile Analyse(RasterImage image, bool hasAlpha)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = image.Pixels;

        var stepX = Math.Max(1, width / 256);
        var stepY = Math.Max(1, height / 256);

        var colorHistogram = new Dictionary<int, int>();
        var quantizedHistogram = new int[QuantizedBuckets];
        double edgeCount = 0;
        double whitespaceCount = 0;
        double luminanceSum = 0;
        double luminanceSqSum = 0;
        double samples = 0;

        for (var y = 0; y < height; y += stepY)
        {
            var nextY = Math.Min(y + stepY, height - 1);
            for (var x = 0; x < width; x += stepX)
            {
                var nextX = Math.Min(x + stepX, width - 1);
                var index = (y * width + x) * 4;
                var rightIndex = (y * width + nextX) * 4;
                var belowIndex = (nextY * width + x) * 4;

                var luminance = Luminance(pixels, index);
                luminanceSum += luminance;
                luminanceSqSum += luminance * luminance;

                if (pixels[index + 3] < 10 || luminance >= 0.92)
                {
                    whitespaceCount++;
                }

                var gradient = Math.Abs(luminance - Luminance(pixels, rightIndex)) + Math.Abs(luminance - Luminance(pixels, belowIndex));
                if (gradient > 0.12)
                {
                    edgeCount++;
                }

                var packed = (pixels[index] << 16) | (pixels[index + 1] << 8) | pixels[index + 2];
                colorHistogram.TryGetValue(packed, out var seen);
                colorHistogram[packed] = seen + 1;
                quantizedHistogram[Quantize(pixels[index], pixels[index + 1], pixels[index + 2])]++;
                samples++;
            }
        }

        if (samples == 0)
        {
            return new ImageContentProfile(ImageContentKind.Graphic, 0, 0, 0, 0, hasAlpha);
        }

        var uniqueColorRatio = colorHistogram.Count / samples;
        var topCoverage = TopColorCoverage(colorHistogram, samples);
        var quantizedCoverage = TopQuantizedCoverage(quantizedHistogram, samples);
        var edgeDensity = edgeCount / samples;
        var whitespaceRatio = whitespaceCount / samples;
        var meanLuminance = luminanceSum / samples;
        var luminanceVariance = Math.Max(0, luminanceSqSum / samples - meanLuminance * meanLuminance);

        var kind = DetermineKind(edgeDensity, uniqueColorRatio, topCoverage, quantizedCoverage, whitespaceRatio, luminanceVariance, hasAlpha);
        return new ImageContentProfile(kind, edgeDensity, uniqueColorRatio, luminanceVariance, whitespaceRatio, hasAlpha)
        {
            TopColorCoverage = topCoverage,
            QuantizedTopColorCoverage = quantizedCoverage
        };
    }

    internal static ImageContentKind DetermineKind(double edgeDensity, double uniqueColorRatio, double topCoverage, double quantizedCoverage, double whitespaceRatio, double luminanceVariance, bool hasAlpha)
    {
        if (whitespaceRatio >= DocumentWhitespaceThreshold && edgeDensity < 0.25)
        {
            return ImageContentKind.Document;
        }

        // Flat-colour artwork, UI screenshots and icons: a handful of colours cover nearly every pixel.
        if (topCoverage >= GraphicTopColorCoverage
            || (quantizedCoverage >= GraphicQuantizedTopColorCoverage && edgeDensity >= GraphicQuantizedMinEdgeDensity))
        {
            return ImageContentKind.Graphic;
        }

        if (hasAlpha && uniqueColorRatio <= GraphicUniqueColorThreshold && edgeDensity <= GraphicEdgeThreshold)
        {
            return ImageContentKind.Graphic;
        }

        if (uniqueColorRatio <= GraphicUniqueColorThreshold && luminanceVariance < 0.015)
        {
            return ImageContentKind.Graphic;
        }

        return ImageContentKind.Photograph;
    }

    private static double TopColorCoverage(Dictionary<int, int> histogram, double samples)
    {
        if (histogram.Count <= TopColorCount)
        {
            return 1.0;
        }

        var counts = new int[histogram.Count];
        histogram.Values.CopyTo(counts, 0);
        Array.Sort(counts);

        double covered = 0;
        for (var i = 0; i < TopColorCount; i++)
        {
            covered += counts[counts.Length - 1 - i];
        }

        return covered / samples;
    }

    private static int Quantize(byte r, byte g, byte b)
        => ((r >> QuantizeShift) << (2 * (8 - QuantizeShift))) | ((g >> QuantizeShift) << (8 - QuantizeShift)) | (b >> QuantizeShift);

    private static double TopQuantizedCoverage(int[] histogram, double samples)
    {
        // Partial selection of the TopColorCount largest buckets: one pass, no sort of 32768 entries.
        Span<int> top = stackalloc int[TopColorCount];
        foreach (var count in histogram)
        {
            if (count <= top[0])
            {
                continue;
            }

            // top[0] is the smallest kept count; replace it and sift it back into place.
            top[0] = count;
            var i = 0;
            while (i + 1 < top.Length && top[i] > top[i + 1])
            {
                (top[i], top[i + 1]) = (top[i + 1], top[i]);
                i++;
            }
        }

        double covered = 0;
        foreach (var count in top)
        {
            covered += count;
        }

        return covered / samples;
    }

    private static double Luminance(byte[] pixels, int index)
        => (pixels[index] * 0.2126 + pixels[index + 1] * 0.7152 + pixels[index + 2] * 0.0722) / 255.0;
}
