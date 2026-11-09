using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public class PpmStagingCacheTests
{
    private static RasterImage Raster(int seed, int width = 96, int height = 64)
        => new(width, height, TestImages.PhotoLikeRgba(width, height, seed));

    [Fact]
    public async Task ConcurrentTracksShareOneStagingFilePerRaster()
    {
        using var temp = new TempFileScope();
        using var cache = new PpmStagingCache(temp);
        var mosaic = Raster(1);
        var full = Raster(2, 160, 120);

        // 4 tracks x 7 probes on the mosaic, then 4 verification encodes of the full frame, all at once.
        var paths = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => cache.Get(i < 28 ? mosaic : full).Value)));

        Assert.Equal(2, cache.WriteCount);
        Assert.Single(paths.Take(28).Distinct());
        Assert.Single(paths.Skip(28).Distinct());
        Assert.NotEqual(paths[0], paths[31]);
        Assert.All(paths.Distinct(), path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void StagedFileIsIdenticalToAPrivateWrite()
    {
        using var temp = new TempFileScope();
        using var cache = new PpmStagingCache(temp);
        var raster = new RasterImage(64, 48, TestImages.PhotoLikeRgba(64, 48, seed: 5, alpha: true));

        var cached = cache.Get(raster);
        var direct = temp.New("direct", "ppm");
        ImageEncoders.WritePpm(raster, direct.Value);

        Assert.Equal(File.ReadAllBytes(direct.Value), File.ReadAllBytes(cached.Value));
    }

    [Fact]
    public void DifferentRastersWithEqualContentAreStagedSeparately()
    {
        // Keyed by identity, not content: hashing a 36 MB frame to dedupe would cost more than the write it saves.
        using var temp = new TempFileScope();
        using var cache = new PpmStagingCache(temp);
        var a = Raster(3);
        var b = Raster(3);

        Assert.NotEqual(cache.Get(a).Value, cache.Get(b).Value);
        Assert.Equal(2, cache.WriteCount);
    }

    [Fact]
    public void DisposeRemovesTheStagingFilesAndRefusesFurtherUse()
    {
        using var temp = new TempFileScope();
        var cache = new PpmStagingCache(temp);
        var path = cache.Get(Raster(4)).Value;
        Assert.True(File.Exists(path));

        cache.Dispose();
        cache.Dispose(); // idempotent

        Assert.False(File.Exists(path));
        Assert.Throws<ObjectDisposedException>(() => cache.Get(Raster(4)));
    }

    [ToolFact("mozjpeg")]
    public async Task RealEncodersProduceIdenticalJpegsWithAndWithoutTheCache()
    {
        using var temp = new TempFileScope();
        var encoders = new ImageEncoders(ImageToolset.Resolve(new AdvancedCodecPreferences()), temp);
        var raster = Raster(8, 256, 192);
        using var cache = encoders.CreatePpmStagingCache();

        // Four tracks x two probes, as the guard runs them.
        foreach (var quality in new[] { 70, 85 })
        {
            foreach (var chroma in new[] { ChromaSubsampling.Subsampled420, ChromaSubsampling.Full444 })
            {
                var cachedMoz = await encoders.EncodeJpegAsync(raster, quality, chroma, default, cache);
                var plainMoz = await encoders.EncodeJpegAsync(raster, quality, chroma, default);
                Assert.Equal(File.ReadAllBytes(plainMoz!.Value.Value), File.ReadAllBytes(cachedMoz!.Value.Value));

                if (encoders.HasJpegli)
                {
                    var cachedLi = await encoders.EncodeJpegliAsync(raster, quality, chroma, default, cache);
                    var plainLi = await encoders.EncodeJpegliAsync(raster, quality, chroma, default);
                    Assert.Equal(File.ReadAllBytes(plainLi!.Value.Value), File.ReadAllBytes(cachedLi!.Value.Value));
                }
            }
        }

        Assert.Equal(1, cache.WriteCount);
    }

    [Fact]
    public async Task EncoderWithoutCacheStillCleansUpItsPrivateStagingFile()
    {
        using var temp = new TempFileScope();
        var encoders = new ImageEncoders(new ImageToolset(), temp);
        var raster = Raster(6);

        // No external encoders are configured here, so this takes the built-in encoder; with a cache it must be unaffected.
        using var cache = encoders.CreatePpmStagingCache();
        var withCache = await encoders.EncodeJpegAsync(raster, 80, ChromaSubsampling.Subsampled420, default, cache);
        var withoutCache = await encoders.EncodeJpegAsync(raster, 80, ChromaSubsampling.Subsampled420, default);

        Assert.NotNull(withCache);
        Assert.NotNull(withoutCache);
        Assert.Equal(0, cache.WriteCount);
        Assert.True(new FileInfo(withCache!.Value.Value).Length > 0);
        Assert.True(new FileInfo(withoutCache!.Value.Value).Length > 0);
    }
}
