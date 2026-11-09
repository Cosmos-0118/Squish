using Squish.Core.Imaging;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class ScoringMemoryTests
{
    // 3000x2000 splits into tiles of 1008/992/1000 x 1008/992 px: neighbours differ slightly in size, as they do in real photos.
    private const int Width = 3000;
    private const int Height = 2000;

    /// <summary>
    /// A frame past <see cref="PerceptualReference.MaxCachedFullCoveragePixels"/> is scored tile by tile without caching its
    /// pyramid. That path used to allocate about 70 MB of fresh Large Object Heap planes per tile, per score; it must now
    /// borrow them from the scratch pool, so the calling thread allocates a fraction of that however often it scores.
    /// </summary>
    [Fact]
    public void UncachedFullCoverageScoringReusesItsPlanesInsteadOfAllocatingThem()
    {
        var reference = new RasterImage(Width, Height, TestImages.PhotoLikeRgba(Width, Height));
        var candidate = new RasterImage(Width, Height, TestImages.PhotoLikeRgba(Width, Height, seed: 8));
        Assert.True(reference.PixelCount > PerceptualReference.MaxCachedFullCoveragePixels);
        var full = PerceptualReference.CreateFullCoverage(reference);
        Assert.True(full.Tiles.Count > 1);

        var warm = full.Score(candidate);

        // Allocations on this thread only: other tests run alongside, and the planes are rented on the calling thread.
        // The pool is process-wide and a concurrent test may trim or drain it mid-run, so judge the best of a few runs;
        // without pooling every run allocates several hundred MB.
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var score = full.Score(candidate);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(warm, score, 9);
        }

        Assert.True(best < 150L * 1024 * 1024, $"scoring allocated {best / (1024 * 1024)} MB on the calling thread");
    }

    [Fact]
    public void UncachedFullCoverageScoreMatchesScoringEachTileOnItsOwn()
    {
        var reference = new RasterImage(Width, Height, TestImages.PhotoLikeRgba(Width, Height));
        var candidate = new RasterImage(Width, Height, TestImages.PhotoLikeRgba(Width, Height, seed: 8));
        var full = PerceptualReference.CreateFullCoverage(reference);

        // Plain, unpooled, cropped scoring of every tile: the reference answer.
        var expected = new double[full.Tiles.Count];
        for (var i = 0; i < expected.Length; i++)
        {
            var tile = full.Tiles[i];
            var r = reference.Crop(tile.X, tile.Y, tile.Width, tile.Height);
            var c = candidate.Crop(tile.X, tile.Y, tile.Width, tile.Height);
            expected[i] = Ssimulacra2.PrepareOpaqueRgba(r.Pixels, r.Width, r.Height).Compare(c.Pixels);
        }

        Assert.Equal(PerceptualReference.Aggregate(expected), full.Score(candidate), 9);
    }

    [Theory]
    [InlineData(21_250)]
    [InlineData(999_936)]
    [InlineData(1_008_000)]
    [InlineData(1_016_064)]
    [InlineData(1_048_576)]
    [InlineData(1_048_577)]
    [InlineData(6_000_000)]
    public void SizeClassesHoldTheRequestWithBoundedSlack(int length)
    {
        var size = ScratchPool<float>.SizeClass(length);

        Assert.InRange(size, length, (int)(length * 1.26));
    }

    [Fact]
    public void NeighbouringTileSizesShareASizeClass()
    {
        Assert.Equal(ScratchPool<float>.SizeClass(992 * 1008), ScratchPool<float>.SizeClass(1008 * 1008));
        Assert.Equal(ScratchPool<float>.SizeClass(1000 * 1008), ScratchPool<float>.SizeClass(1008 * 1008));
    }

    [Fact]
    public void PooledPreparedReferenceCannotBeUsedAfterDispose()
    {
        var pixels = TestImages.PhotoLikeRgba(64, 64);
        var prepared = Ssimulacra2.PrepareOpaqueRgba(pixels, 64, 64, pooled: true);
        Assert.Equal(100.0, prepared.Compare(pixels), 6);

        prepared.Dispose();
        prepared.Dispose();

        Assert.Throws<ObjectDisposedException>(() => prepared.Compare(pixels));
    }
}
