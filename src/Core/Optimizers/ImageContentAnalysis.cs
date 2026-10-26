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
}

/// <summary>
/// Cheap content classifier (photo vs. flat graphic vs. text document) driving codec and chroma-subsampling
/// choices. It samples at most ~256x256 points so it stays negligible next to the encode itself.
/// </summary>
internal static class ImageContentAnalyzer
{
    private const double DocumentWhitespaceThreshold = 0.52;
    private const double GraphicUniqueColorThreshold = 0.18;
    private const double GraphicEdgeThreshold = 0.12;
    private const double GraphicTopColorCoverage = 0.90;
    private const int TopColorCount = 48;

    public static ImageContentProfile Analyse(RasterImage image, bool hasAlpha)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = image.Pixels;

        var stepX = Math.Max(1, width / 256);
        var stepY = Math.Max(1, height / 256);

        var colorHistogram = new Dictionary<int, int>();
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
                samples++;
            }
        }

        if (samples == 0)
        {
            return new ImageContentProfile(ImageContentKind.Graphic, 0, 0, 0, 0, hasAlpha);
        }

        var uniqueColorRatio = colorHistogram.Count / samples;
        var topCoverage = TopColorCoverage(colorHistogram, samples);
        var edgeDensity = edgeCount / samples;
        var whitespaceRatio = whitespaceCount / samples;
        var meanLuminance = luminanceSum / samples;
        var luminanceVariance = Math.Max(0, luminanceSqSum / samples - meanLuminance * meanLuminance);

        var kind = DetermineKind(edgeDensity, uniqueColorRatio, topCoverage, whitespaceRatio, luminanceVariance, hasAlpha);
        return new ImageContentProfile(kind, edgeDensity, uniqueColorRatio, luminanceVariance, whitespaceRatio, hasAlpha);
    }

    private static ImageContentKind DetermineKind(double edgeDensity, double uniqueColorRatio, double topCoverage, double whitespaceRatio, double luminanceVariance, bool hasAlpha)
    {
        if (whitespaceRatio >= DocumentWhitespaceThreshold && edgeDensity < 0.25)
        {
            return ImageContentKind.Document;
        }

        // Flat-colour artwork, UI screenshots and icons: a handful of colours cover nearly every pixel.
        if (topCoverage >= GraphicTopColorCoverage)
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

    private static double Luminance(byte[] pixels, int index)
        => (pixels[index] * 0.2126 + pixels[index + 1] * 0.7152 + pixels[index + 2] * 0.0722) / 255.0;
}
