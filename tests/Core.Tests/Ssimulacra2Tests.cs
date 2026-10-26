using System;
using Squish.Core.Imaging;
using Xunit;

namespace Core.Tests;

/// <summary>
/// The managed SSIMULACRA2 port is validated against scores produced by the reference C++ implementation
/// (libjxl's <c>ssimulacra2</c>) for deterministic synthetic image pairs.
/// </summary>
public sealed class Ssimulacra2Tests
{
    private const int Size = 64;

    [Theory]
    [InlineData("gradient_vs_uniform", -64.56872148, 0.05)]
    [InlineData("noise_vs_uniform", -47.33572558, 0.05)]
    [InlineData("noise_vs_sharpen", -5.81017556, 0.05)]
    [InlineData("edge_vs_boxblur", 24.27333552, 0.2)]
    // Smooth, near-identical pairs sit on a float32 cancellation knife edge in the reference; allow more slack.
    [InlineData("gradient_vs_boxblur", 94.34090315, 0.6)]
    [InlineData("gradient_vs_yuv", 97.26010964, 0.8)]
    public void MatchesReferenceImplementation(string scenario, double expected, double tolerance)
    {
        var (reference, distorted) = BuildPair(scenario);
        var score = Ssimulacra2.Compute(reference, distorted, Size, Size, 3);
        Assert.InRange(score, expected - tolerance, expected + tolerance);
    }

    [Fact]
    public void IdenticalImagesScoreOneHundred()
    {
        var image = Noise(Size, Size, 42);
        Assert.Equal(100.0, Ssimulacra2.Compute(image, image, Size, Size, 3), 6);
    }

    [Fact]
    public void PreparedReferenceScoresMatchOneShotScoresOnEveryReuse()
    {
        var reference = Noise(Size, Size, 9);
        var prepared = Ssimulacra2.Prepare(reference, Size, Size, 3);
        var (_, blurred) = BuildPair("noise_vs_sharpen");
        var shifted = (byte[])reference.Clone();
        for (var i = 0; i < shifted.Length; i += 5)
        {
            shifted[i] = (byte)Math.Clamp(shifted[i] + 12, 0, 255);
        }

        // Interleave candidates so any state leaking between comparisons would show up.
        foreach (var candidate in new[] { blurred, shifted, blurred, reference, shifted })
        {
            Assert.Equal(Ssimulacra2.Compute(reference, candidate, Size, Size, 3), prepared.Compare(candidate));
        }
    }

    [Fact]
    public void ScoreDropsAsDistortionGrows()
    {
        var reference = Noise(Size, Size, 5);
        double previous = 101;
        foreach (var amount in new[] { 1, 6, 20, 60 })
        {
            var distorted = (byte[])reference.Clone();
            for (var i = 0; i < distorted.Length; i += 3)
            {
                distorted[i] = (byte)Math.Clamp(distorted[i] + amount * (i % 2 == 0 ? 1 : -1), 0, 255);
            }

            var score = Ssimulacra2.Compute(reference, distorted, Size, Size, 3);
            Assert.True(score < previous, $"score {score} should be below {previous} for amount {amount}");
            previous = score;
        }
    }

    [Fact]
    public void TransparentImagesAreScoredAgainstBothBackdrops()
    {
        var rgba = new byte[Size * Size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 1] = 40;
            rgba[i + 2] = 40;
            rgba[i + 3] = (byte)(i / 4 % Size * 4);
        }

        var identical = Ssimulacra2.Compute(rgba, rgba, Size, Size, 4);
        Assert.Equal(100.0, identical, 6);

        var faded = (byte[])rgba.Clone();
        for (var i = 3; i < faded.Length; i += 4)
        {
            faded[i] = (byte)(faded[i] / 2);
        }

        Assert.True(Ssimulacra2.Compute(rgba, faded, Size, Size, 4) < 95);
    }

    [Fact]
    public void RejectsImagesSmallerThanEightPixels()
    {
        var tiny = new byte[7 * 7 * 3];
        Assert.Throws<ArgumentException>(() => Ssimulacra2.Compute(tiny, tiny, 7, 7, 3));
    }

    private static (byte[] Reference, byte[] Distorted) BuildPair(string scenario)
    {
        var uniform = Uniform(Size, Size, 128);
        switch (scenario)
        {
            case "gradient_vs_uniform":
                return (GradientH(Size, Size), uniform);
            case "noise_vs_uniform":
                return (Noise(Size, Size, 42), uniform);
            case "gradient_vs_boxblur":
                var gradient = GradientH(Size, Size);
                return (gradient, BoxBlur(gradient, Size, Size));
            case "noise_vs_sharpen":
                var noise = Noise(Size, Size, 999);
                return (noise, Sharpen(noise, Size, Size));
            case "gradient_vs_yuv":
                var diagonal = GradientDiagonal(Size, Size);
                return (diagonal, YuvRoundTrip(diagonal, Size));
            case "edge_vs_boxblur":
                var edge = EdgeVertical(Size, Size);
                return (edge, BoxBlur(edge, Size, Size));
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    private static byte[] Uniform(int w, int h, byte value)
    {
        var data = new byte[w * h * 3];
        Array.Fill(data, value);
        return data;
    }

    private static byte[] GradientH(int w, int h)
    {
        var data = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var value = (byte)(x * 255 / (w - 1));
                for (var c = 0; c < 3; c++)
                {
                    data[(y * w + x) * 3 + c] = value;
                }
            }
        }

        return data;
    }

    private static byte[] GradientDiagonal(int w, int h)
    {
        var data = new byte[w * h * 3];
        var max = w + h - 2;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var value = (byte)((x + y) * 255 / max);
                for (var c = 0; c < 3; c++)
                {
                    data[(y * w + x) * 3 + c] = value;
                }
            }
        }

        return data;
    }

    private static byte[] EdgeVertical(int w, int h)
    {
        var data = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var value = (byte)(x < w / 2 ? 0 : 255);
                for (var c = 0; c < 3; c++)
                {
                    data[(y * w + x) * 3 + c] = value;
                }
            }
        }

        return data;
    }

    /// <summary>Same LCG as the reference test-suite generator.</summary>
    private static byte[] Noise(int w, int h, ulong seed)
    {
        var data = new byte[w * h * 3];
        var state = seed;
        for (var i = 0; i < data.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            data[i] = (byte)((state >> 33) & 0xFF);
        }

        return data;
    }

    private static byte[] BoxBlur(byte[] input, int w, int h)
    {
        var output = new byte[input.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = new int[3];
                var count = 0;
                for (var ky = -4; ky < 4; ky++)
                {
                    for (var kx = -4; kx < 4; kx++)
                    {
                        var ny = Math.Clamp(y + ky, 0, h - 1);
                        var nx = Math.Clamp(x + kx, 0, w - 1);
                        for (var c = 0; c < 3; c++)
                        {
                            sum[c] += input[(ny * w + nx) * 3 + c];
                        }

                        count++;
                    }
                }

                for (var c = 0; c < 3; c++)
                {
                    output[(y * w + x) * 3 + c] = (byte)(sum[c] / count);
                }
            }
        }

        return output;
    }

    private static byte[] Sharpen(byte[] input, int w, int h)
    {
        var output = new byte[input.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var index = (y * w + x) * 3 + c;
                    int centre = input[index];
                    var top = y > 0 ? input[((y - 1) * w + x) * 3 + c] : centre;
                    var bottom = y < h - 1 ? input[((y + 1) * w + x) * 3 + c] : centre;
                    var left = x > 0 ? input[(y * w + x - 1) * 3 + c] : centre;
                    var right = x < w - 1 ? input[(y * w + x + 1) * 3 + c] : centre;
                    output[index] = (byte)Math.Clamp(5 * centre - top - bottom - left - right, 0, 255);
                }
            }
        }

        return output;
    }

    private static byte[] YuvRoundTrip(byte[] input, int size)
    {
        var output = new byte[input.Length];
        for (var i = 0; i < size * size; i++)
        {
            float r = input[i * 3], g = input[i * 3 + 1], b = input[i * 3 + 2];
            var y = 0.299f * r + 0.587f * g + 0.114f * b;
            var u = -0.14713f * r - 0.28886f * g + 0.436f * b + 128f;
            var v = 0.615f * r - 0.51499f * g - 0.10001f * b + 128f;
            output[i * 3] = (byte)Math.Clamp(y + 1.13983f * (v - 128f), 0f, 255f);
            output[i * 3 + 1] = (byte)Math.Clamp(y - 0.39465f * (u - 128f) - 0.58060f * (v - 128f), 0f, 255f);
            output[i * 3 + 2] = (byte)Math.Clamp(y + 2.03211f * (u - 128f), 0f, 255f);
        }

        return output;
    }
}
