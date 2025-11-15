using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ClopWindows.Core.Optimizers;

internal static class ImagePerceptualQualityGuard
{
    private const double C1 = 6.5025; // (0.01 * 255)^2
    private const double C2 = 58.5225; // (0.03 * 255)^2

    public static double ComputeStructuralSimilarity(Image<Rgba32> reference, Image<Rgba32> candidate)
    {
        if (reference.Width <= 0 || reference.Height <= 0)
        {
            return 1.0;
        }

        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
        {
            using var resized = candidate.Clone(context => context.Resize(reference.Width, reference.Height));
            return ComputeStructuralSimilarityInternal(reference, resized);
        }

        return ComputeStructuralSimilarityInternal(reference, candidate);
    }

    private static double ComputeStructuralSimilarityInternal(Image<Rgba32> reference, Image<Rgba32> candidate)
    {
        var width = reference.Width;
        var height = reference.Height;
        var pixelCount = (double)(width * height);

        double sumRef = 0;
        double sumCandidate = 0;
        double sumRefSq = 0;
        double sumCandidateSq = 0;
        double sumCross = 0;

        for (var y = 0; y < height; y++)
        {
            var referenceSpan = reference.DangerousGetPixelRowMemory(y).Span;
            var candidateSpan = candidate.DangerousGetPixelRowMemory(y).Span;

            for (var x = 0; x < width; x++)
            {
                var refPixel = referenceSpan[x];
                var candidatePixel = candidateSpan[x];

                var refLuma = ToLuminance(refPixel);
                var candidateLuma = ToLuminance(candidatePixel);

                sumRef += refLuma;
                sumCandidate += candidateLuma;
                sumRefSq += refLuma * refLuma;
                sumCandidateSq += candidateLuma * candidateLuma;
                sumCross += refLuma * candidateLuma;
            }
        }

        var meanRef = sumRef / pixelCount;
        var meanCandidate = sumCandidate / pixelCount;

        var varianceRef = Math.Max(0, (sumRefSq / pixelCount) - (meanRef * meanRef));
        var varianceCandidate = Math.Max(0, (sumCandidateSq / pixelCount) - (meanCandidate * meanCandidate));
        var covariance = (sumCross / pixelCount) - (meanRef * meanCandidate);

        var numerator = (2 * meanRef * meanCandidate + C1) * (2 * covariance + C2);
        var denominator = (meanRef * meanRef + meanCandidate * meanCandidate + C1) * (varianceRef + varianceCandidate + C2);

        if (denominator <= double.Epsilon)
        {
            return 1.0;
        }

        var ssim = numerator / denominator;
        if (double.IsNaN(ssim))
        {
            return 1.0;
        }

        return Math.Clamp(ssim, 0, 1);
    }

    private static double ToLuminance(Rgba32 pixel)
    {
        const double scale = 1.0 / 255.0;
        var r = pixel.R * scale;
        var g = pixel.G * scale;
        var b = pixel.B * scale;
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }
}
