using System;
using Squish.Core.Imaging;
using Xunit;

namespace Core.Tests;

/// <summary>
/// The 64 px reference pairs never reach the row-parallel blur or the multi-scale buffer reuse that real screenshots
/// do, so these pin the large-image behaviour: scores must not depend on how the work is scheduled.
/// </summary>
public sealed class Ssimulacra2LargeImageTests
{
    private const int Width = 640;
    private const int Height = 480;

    [Fact]
    public void LargeImageScoreIsPinnedToTheAllocationHeavyImplementation()
    {
        var (reference, distorted) = Pair();

        // Recorded from the previous implementation (fresh float[] per channel per scale, sequential blur).
        Assert.Equal(PinnedScore, Ssimulacra2.Compute(reference, distorted, Width, Height, 3), 3);
    }

    [Fact]
    public void OpaqueRgbaMatchesPackedRgb()
    {
        var (reference, distorted) = Pair();
        var packed = Ssimulacra2.Compute(reference, distorted, Width, Height, 3);
        var rgba = Ssimulacra2.ComputeOpaqueRgba(ToRgba(reference), ToRgba(distorted), Width, Height);
        Assert.Equal(packed, rgba, 9);
    }

    [Fact]
    public void PreparedReferenceIsReusableOnLargeImages()
    {
        var (reference, distorted) = Pair();
        var prepared = Ssimulacra2.PrepareOpaqueRgba(ToRgba(reference), Width, Height);
        var candidate = ToRgba(distorted);
        var first = prepared.Compare(candidate);
        Assert.Equal(first, prepared.Compare(candidate), 9);
        Assert.Equal(Ssimulacra2.Compute(reference, distorted, Width, Height, 3), first, 9);
    }

    // Filled in from the pre-change implementation; recorded at 640x480.
    private const double PinnedScore = 58.886725055644895;

    private static (byte[] Reference, byte[] Distorted) Pair()
    {
        var reference = new byte[Width * Height * 3];
        var distorted = new byte[reference.Length];
        var state = 0x9E3779B97F4A7C15UL;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
                var noise = (int)((state >> 33) & 0x1F);
                var o = (y * Width + x) * 3;
                reference[o] = (byte)(x * 255 / Width);
                reference[o + 1] = (byte)(y * 255 / Height);
                reference[o + 2] = (byte)(((x / 16 + y / 16) & 1) * 200 + 20);
                distorted[o] = (byte)Math.Clamp(reference[o] + noise - 16, 0, 255);
                distorted[o + 1] = (byte)Math.Clamp(reference[o + 1] + (noise >> 1) - 8, 0, 255);
                distorted[o + 2] = reference[o + 2];
            }
        }

        return (reference, distorted);
    }

    private static byte[] ToRgba(byte[] rgb)
    {
        var rgba = new byte[rgb.Length / 3 * 4];
        for (int i = 0, s = 0, d = 0; s < rgb.Length; i++, s += 3, d += 4)
        {
            rgba[d] = rgb[s];
            rgba[d + 1] = rgb[s + 1];
            rgba[d + 2] = rgb[s + 2];
            rgba[d + 3] = 255;
        }

        return rgba;
    }
}
