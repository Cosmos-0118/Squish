using System;
using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace ClopWindows.Core.Optimizers;

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

internal static class ImageContentAnalyzer
{
    private const double DocumentWhitespaceThreshold = 0.52;
    private const double GraphicUniqueColorThreshold = 0.18;
    private const double GraphicEdgeThreshold = 0.12;

    public static ImageContentProfile Analyse(Image<Rgba32> image, bool hasAlpha)
    {
        if (image.Width == 0 || image.Height == 0)
        {
            return new ImageContentProfile(ImageContentKind.Graphic, 0, 0, 0, 0, hasAlpha);
        }

        var totalPixels = (double)(image.Width * image.Height);
        var stepX = Math.Max(1, image.Width / 256);
        var stepY = Math.Max(1, image.Height / 256);

        var colors = new HashSet<int>();
        double edgeCount = 0;
        double whitespaceCount = 0;
        double luminanceSum = 0;
        double luminanceSqSum = 0;

        for (var y = 0; y < image.Height; y += stepY)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            var nextRow = y + stepY < image.Height ? image.DangerousGetPixelRowMemory(y + stepY).Span : row;

            for (var x = 0; x < image.Width; x += stepX)
            {
                var pixel = row[x];
                var nextX = x + stepX < image.Width ? row[x + stepX] : pixel;
                var diagonal = nextRow[Math.Min(x, nextRow.Length - 1)];

                var luminance = ToLuminance(pixel);
                luminanceSum += luminance;
                luminanceSqSum += luminance * luminance;

                if (IsWhitespace(pixel))
                {
                    whitespaceCount++;
                }

                var gradient = Math.Abs(luminance - ToLuminance(nextX)) + Math.Abs(luminance - ToLuminance(diagonal));
                if (gradient > 0.12)
                {
                    edgeCount++;
                }

                colors.Add(ToPackedRgb(pixel));
            }
        }

        var uniqueColorRatio = colors.Count / Math.Min(colors.Count + 1.0, totalPixels);
        var edgeDensity = edgeCount / Math.Max(1.0, (image.Width / (double)stepX) * (image.Height / (double)stepY));
        var whitespaceRatio = whitespaceCount / Math.Max(1.0, (image.Width / (double)stepX) * (image.Height / (double)stepY));

        var meanLuminance = luminanceSum / Math.Max(1.0, (image.Width / (double)stepX) * (image.Height / (double)stepY));
        var luminanceVariance = Math.Max(0, (luminanceSqSum / Math.Max(1.0, (image.Width / (double)stepX) * (image.Height / (double)stepY))) - (meanLuminance * meanLuminance));

        var kind = DetermineKind(edgeDensity, uniqueColorRatio, whitespaceRatio, luminanceVariance, hasAlpha);
        return new ImageContentProfile(kind, edgeDensity, uniqueColorRatio, luminanceVariance, whitespaceRatio, hasAlpha);
    }

    private static ImageContentKind DetermineKind(double edgeDensity, double uniqueColorRatio, double whitespaceRatio, double luminanceVariance, bool hasAlpha)
    {
        if (whitespaceRatio >= DocumentWhitespaceThreshold && edgeDensity < 0.25)
        {
            return ImageContentKind.Document;
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

    private static bool IsWhitespace(Rgba32 pixel)
    {
        if (pixel.A < 10)
        {
            return true;
        }

        var luma = ToLuminance(pixel);
        return luma >= 0.92;
    }

    private static double ToLuminance(Rgba32 pixel)
    {
        const double scale = 1.0 / 255.0;
        return (pixel.R * 0.2126 * scale) + (pixel.G * 0.7152 * scale) + (pixel.B * 0.0722 * scale);
    }

    private static int ToPackedRgb(Rgba32 pixel) => (pixel.R << 16) | (pixel.G << 8) | pixel.B;
}
