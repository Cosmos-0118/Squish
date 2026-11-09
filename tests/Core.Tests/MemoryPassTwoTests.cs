using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Pdf;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Second memory pass: pooled rasters, crop-free mosaic scoring, one-shot metric, shared pool ceiling, bounded heavy jobs and
/// per-run scratch directories. Each test pins a behaviour the lower peak-memory numbers depend on.
/// </summary>
public sealed class MemoryPassTwoTests
{
    // ---- one-shot metric ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(64, 64)]
    [InlineData(301, 197)]
    [InlineData(520, 333)]
    public void OneShotOpaqueScoreMatchesPreparedReferenceExactly(int width, int height)
    {
        var a = TestImages.PhotoLikeRgba(width, height);
        var b = TestImages.PhotoLikeRgba(width, height, seed: 11);

        var oneShot = Ssimulacra2.ComputeOpaqueRgba(a, b, width, height);
        var prepared = Ssimulacra2.PrepareOpaqueRgba(a, width, height).Compare(b);

        Assert.Equal(prepared, oneShot, 9);
    }

    [Fact]
    public void OneShotTransparentScoreMatchesPreparedReferenceExactly()
    {
        const int width = 211;
        const int height = 143;
        var a = TestImages.PhotoLikeRgba(width, height, alpha: true);
        var b = TestImages.PhotoLikeRgba(width, height, seed: 3, alpha: true);

        var oneShot = Ssimulacra2.Compute(a, b, width, height, 4);
        var prepared = Ssimulacra2.Prepare(a, width, height, 4).Compare(b);

        Assert.Equal(prepared, oneShot, 9);
    }

    [Fact]
    public void OneShotRgbScoreMatchesPreparedReferenceExactly()
    {
        const int width = 130;
        const int height = 90;
        var rgb = new RasterImage(width, height, TestImages.PhotoLikeRgba(width, height)).ToRgb();
        var other = new RasterImage(width, height, TestImages.PhotoLikeRgba(width, height, seed: 5)).ToRgb();

        Assert.Equal(Ssimulacra2.Prepare(rgb, width, height, 3).Compare(other), Ssimulacra2.Compute(rgb, other, width, height, 3), 9);
    }

    [Fact]
    public void OneShotScoreReturnsItsPlanesToThePool()
    {
        const int width = 1000;
        const int height = 1000;
        var a = TestImages.PhotoLikeRgba(width, height);
        var b = TestImages.PhotoLikeRgba(width, height, seed: 2);
        Ssimulacra2.ComputeOpaqueRgba(a, b, width, height);

        // Warm pool: further comparisons of the same size barely allocate on this thread.
        var best = long.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Ssimulacra2.ComputeOpaqueRgba(a, b, width, height);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(best < 40L * 1024 * 1024, $"one-shot comparison allocated {best / (1024 * 1024)} MB");
    }

    // ---- pooled rasters -----------------------------------------------------------------------------------------

    [Fact]
    public void PooledRasterExposesExactlyItsPixelsAndSurvivesDoubleDispose()
    {
        var raster = RasterImage.Rent(300, 200);

        Assert.True(raster.IsPooled);
        Assert.Equal(300 * 200 * 4, raster.Span.Length);
        Assert.True(raster.Pixels.Length >= raster.Span.Length);

        raster.Dispose();
        raster.Dispose();
    }

    [Fact]
    public void PooledAndPlainDecodesAreByteIdentical()
    {
        var rgba = TestImages.PhotoLikeRgba(320, 240);
        var file = TestImages.SavePng(rgba, 320, 240);
        try
        {
            var plain = ImageIO.DecodeToRaster(file.Value);
            using var pooled = ImageIO.DecodeToRaster(file.Value, pooled: true);

            Assert.False(plain.IsPooled);
            Assert.True(pooled.IsPooled);
            Assert.True(plain.Span.SequenceEqual(pooled.Span));
            Assert.True(plain.Span.SequenceEqual(rgba));
        }
        finally
        {
            TestImages.TryDelete(file);
        }
    }

    [Fact]
    public void StripWiseDecodeCoversEveryRowOfAWideImage()
    {
        // Wider than the strip budget allows more than one row at a time.
        const int width = 20_000;
        const int height = 9;
        var rgba = TestImages.GraphicRgba(width, height);
        var file = TestImages.SavePng(rgba, width, height);
        try
        {
            using var decoded = ImageIO.DecodeToRaster(file.Value, pooled: true);

            Assert.True(decoded.Span.SequenceEqual(rgba));
        }
        finally
        {
            TestImages.TryDelete(file);
        }
    }

    [Fact]
    public void HasVisibleAlphaIgnoresStaleBytesPastAPooledBuffer()
    {
        var raster = RasterImage.Rent(300, 300);
        var span = raster.Span;
        span.Fill(255);
        raster.Pixels.AsSpan(span.Length).Fill(0);

        Assert.False(raster.HasVisibleAlpha());
        raster.Dispose();
    }

    // ---- mosaic scoring ------------------------------------------------------------------------------------------

    [Fact]
    public void MosaicScoreMatchesCroppingEachTile()
    {
        const int width = 1600;
        const int height = 1200;
        var reference = new RasterImage(width, height, TestImages.PhotoLikeRgba(width, height));
        using var perceptual = PerceptualReference.Create(reference);
        Assert.False(perceptual.IsWholeImage);

        var mosaic = perceptual.Mosaic;
        var distorted = new RasterImage(mosaic.Width, mosaic.Height, TestImages.PhotoLikeRgba(mosaic.Width, mosaic.Height, seed: 9));

        var expected = new double[perceptual.Tiles.Count];
        var offset = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var tile = perceptual.Tiles[i];
            var original = reference.Crop(tile.X, tile.Y, tile.Width, tile.Height);
            var candidate = distorted.Crop(0, offset, tile.Width, tile.Height);
            expected[i] = Ssimulacra2.PrepareOpaqueRgba(original.Pixels, original.Width, original.Height).Compare(candidate.Pixels);
            offset += tile.Height;
        }

        Assert.Equal(PerceptualReference.Aggregate(expected), perceptual.ScoreMosaic(distorted), 9);
    }

    [Fact]
    public void MosaicIsBuiltOnceAndSharedByEveryCaller()
    {
        const int width = 1600;
        const int height = 1200;
        using var perceptual = PerceptualReference.Create(new RasterImage(width, height, TestImages.PhotoLikeRgba(width, height)));

        Assert.Same(perceptual.Mosaic, perceptual.Mosaic);
    }

    [Fact]
    public void ScoringAfterDisposeIsRejectedAndDisposeIsRepeatable()
    {
        const int width = 400;
        const int height = 300;
        var pixels = TestImages.PhotoLikeRgba(width, height);
        var reference = new RasterImage(width, height, pixels);
        var perceptual = PerceptualReference.Create(reference);
        Assert.Equal(100.0, perceptual.Score(reference), 6);

        perceptual.Dispose();
        perceptual.Dispose();

        Assert.Throws<ObjectDisposedException>(() => perceptual.Score(reference));
    }

    [Fact]
    public void ScoringAPooledCandidateIgnoresItsSpareBytes()
    {
        const int width = 400;
        const int height = 300;
        var pixels = TestImages.PhotoLikeRgba(width, height);
        var reference = new RasterImage(width, height, pixels);
        using var perceptual = PerceptualReference.Create(reference);
        using var pooled = RasterImage.Rent(width, height);
        pixels.CopyTo(pooled.Pixels, 0);
        pooled.Pixels.AsSpan(width * height * 4).Fill(0);

        Assert.Equal(100.0, perceptual.Score(pooled), 6);
    }

    // ---- shared pool ceiling -------------------------------------------------------------------------------------

    [Fact]
    public void FloatAndBytePoolsShareOneRetentionCeiling()
    {
        ScratchPools.TrimAll();
        var floats = new List<float[]>();
        var bytes = new List<byte[]>();
        for (var i = 0; i < 24; i++)
        {
            floats.Add(ScratchPool<float>.Rent(4_000_000));
            bytes.Add(ScratchPool<byte>.Rent(16_000_000));
        }

        foreach (var array in floats)
        {
            ScratchPool<float>.Return(array);
        }

        foreach (var array in bytes)
        {
            ScratchPool<byte>.Return(array);
        }

        Assert.InRange(ScratchPools.RetainedBytes, 1, ScratchPools.MaxRetainedBytes);
        ScratchPools.TrimAll();
        Assert.Equal(0, ScratchPools.RetainedBytes);
    }

    [Fact]
    public void BytesReturnedWhileFloatsFillTheCeilingStillDisplaceThem()
    {
        ScratchPools.TrimAll();
        var floats = new List<float[]>();
        for (var i = 0; i < 40; i++)
        {
            floats.Add(ScratchPool<float>.Rent(4_000_000));
        }

        foreach (var array in floats)
        {
            ScratchPool<float>.Return(array);
        }

        // The ceiling is now full of float arrays; a byte array must still be kept, by evicting floats.
        var buffer = ScratchPool<byte>.Rent(20_000_000);
        ScratchPool<byte>.Return(buffer);

        Assert.True(ScratchPools.RetainedBytes <= ScratchPools.MaxRetainedBytes);
        Assert.Same(buffer, ScratchPool<byte>.Rent(20_000_000));
        ScratchPools.TrimAll();
    }

    // ---- prepared tiles are safe to release mid-comparison ---------------------------------------------------------

    [Fact]
    public void DisposingAPreparedReferenceDuringAComparisonKeepsItsPlanesUntilTheComparisonEnds()
    {
        var pixels = TestImages.PhotoLikeRgba(256, 256);
        var prepared = Ssimulacra2.PrepareOpaqueRgba(pixels, 256, 256, pooled: true);

        Assert.True(prepared.TryEnter());
        prepared.Dispose();

        // Disposed, but the comparison that registered first can still finish on intact planes.
        Assert.Equal(100.0, prepared.CompareEntered(pixels, CancellationToken.None), 6);
        prepared.Exit();
        Assert.False(prepared.TryEnter());
        Assert.Throws<ObjectDisposedException>(() => prepared.Compare(pixels));
    }

    // ---- heavy-job gate ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HeavyJobsAreBoundedWhileImagesUseEveryWorker()
    {
        var pdf = new ConcurrencyProbe(ItemType.Pdf);
        var image = new ConcurrencyProbe(ItemType.Image);
        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { pdf, image }, degreeOfParallelism: 6, maxConcurrentHeavyJobs: 2);

        var tickets = new List<OptimisationTicket>();
        for (var i = 0; i < 8; i++)
        {
            tickets.Add(coordinator.Enqueue(new OptimisationRequest(ItemType.Pdf, TempFile())));
        }

        await Task.WhenAll(tickets.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, pdf.MaxConcurrent);

        tickets.Clear();
        for (var i = 0; i < 12; i++)
        {
            tickets.Add(coordinator.Enqueue(new OptimisationRequest(ItemType.Image, TempFile())));
        }

        await Task.WhenAll(tickets.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(image.MaxConcurrent > 2, $"images ran at most {image.MaxConcurrent} at a time");
    }

    [Fact]
    public async Task ImagesDoNotWaitBehindQueuedPdfs()
    {
        var pdf = new ConcurrencyProbe(ItemType.Pdf) { DelayMs = 800 };
        var image = new ConcurrencyProbe(ItemType.Image);
        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { pdf, image }, degreeOfParallelism: 2, maxConcurrentHeavyJobs: 1);

        var pdfs = Enumerable.Range(0, 4).Select(_ => coordinator.Enqueue(new OptimisationRequest(ItemType.Pdf, TempFile()))).ToList();
        var images = Enumerable.Range(0, 6).Select(_ => coordinator.Enqueue(new OptimisationRequest(ItemType.Image, TempFile()))).ToList();

        // All six pictures finish while most PDFs are still queued behind the single heavy worker.
        await Task.WhenAll(images.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(pdfs, t => !t.Completion.IsCompleted);
        await Task.WhenAll(pdfs.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---- per-run scratch directories -----------------------------------------------------------------------------

    [Fact]
    public void TempFileScopeWorksInOneRunDirectoryAndRemovesItWhole()
    {
        string directory;
        FilePath first;
        using (var scope = new TempFileScope())
        {
            first = scope.New("squish-test", "bin");
            var second = scope.New("squish-test", ".bin");
            File.WriteAllText(first.Value, "a");
            File.WriteAllText(second.Value, "b");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(first.Value)!, "unreleased-leftover.txt"), "c");

            directory = Path.GetDirectoryName(first.Value)!;
            Assert.Equal(directory, Path.GetDirectoryName(second.Value));
            Assert.StartsWith(TempRuns.Root, directory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Environment.ProcessId + "-", Path.GetFileName(directory), StringComparison.Ordinal);
            Assert.EndsWith(".bin", second.Value, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void SweepRemovesDirectoriesOfDeadProcessesOnly()
    {
        var root = TempRuns.Root;
        Directory.CreateDirectory(root);
        var dead = Path.Combine(root, "2000000001-" + Guid.NewGuid().ToString("N"));
        var alive = Path.Combine(root, "2000000002-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dead);
        Directory.CreateDirectory(alive);
        File.WriteAllText(Path.Combine(dead, "x.tmp"), "x");
        try
        {
            TempRuns.SweepStale(isProcessAlive: pid => pid == 2000000002);

            Assert.False(Directory.Exists(dead));
            Assert.True(Directory.Exists(alive));
        }
        finally
        {
            TempRuns.TryDelete(dead);
            TempRuns.TryDelete(alive);
        }
    }

    [Fact]
    public void SweepTreatsLongUntouchedDirectoriesAsAbandonedEvenIfTheirPidLives()
    {
        var directory = Path.Combine(TempRuns.Root, "2000000003-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(TempRuns.ShouldSweep(directory, DateTime.UtcNow, _ => true));
            Assert.True(TempRuns.ShouldSweep(directory, DateTime.UtcNow + TempRuns.MaximumAge + TimeSpan.FromMinutes(1), _ => true));
        }
        finally
        {
            TempRuns.TryDelete(directory);
        }
    }

    // ---- PDF JPEG header reads ------------------------------------------------------------------------------------

    [Fact]
    public async Task JpegHeaderReadStopsShortOfTheWholeFile()
    {
        var pixels = TestImages.PhotoLikeRgba(1200, 900);
        var file = TestImages.SaveJpeg(pixels, 1200, 900, 95);
        try
        {
            var (info, header) = await PdfImagePass.ReadJpegHeaderAsync(file.Value, CancellationToken.None);

            Assert.NotNull(info);
            Assert.Equal(1200, info!.Value.Width);
            Assert.Equal(900, info.Value.Height);
            Assert.Equal(3, info.Value.Components);
            Assert.True(header.Length < new FileInfo(file.Value).Length, "the whole file was read");
        }
        finally
        {
            TestImages.TryDelete(file);
        }
    }

    [Fact]
    public async Task JpegHeaderReadWidensForLargeMetadataBlocks()
    {
        // A 200 KB APP1 block ahead of the frame header pushes it past the first window.
        var plain = File.ReadAllBytes(TestImages.SaveJpeg(TestImages.PhotoLikeRgba(200, 150), 200, 150, 90).Value);
        var padding = new byte[200_000];
        var app = new byte[4 + padding.Length];
        app[0] = 0xFF;
        app[1] = 0xE1;
        app[2] = (byte)((padding.Length + 2) >> 8);
        app[3] = (byte)((padding.Length + 2) & 0xFF);
        var withBlock = plain.Take(2).Concat(app).Concat(plain.Skip(2)).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "squish-test-" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, withBlock);
        try
        {
            var (info, _) = await PdfImagePass.ReadJpegHeaderAsync(path, CancellationToken.None);

            Assert.NotNull(info);
            Assert.Equal(200, info!.Value.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JpegHeaderReadOfGarbageReturnsNoInfo()
    {
        var path = Path.Combine(Path.GetTempPath(), "squish-test-" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, new byte[300]);
        try
        {
            var (info, _) = await PdfImagePass.ReadJpegHeaderAsync(path, CancellationToken.None);

            Assert.Null(info);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FilePath TempFile()
    {
        var file = FilePath.TempFile("optimiser-tests", ".tmp", addUniqueSuffix: true);
        file.EnsureParentDirectoryExists();
        File.WriteAllText(file.Value, "demo");
        return file;
    }

    private sealed class ConcurrencyProbe(ItemType itemType) : IOptimiser
    {
        private int _current;
        private int _max;

        public ItemType ItemType { get; } = itemType;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public int DelayMs { get; init; } = 150;

        public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(DelayMs, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }

            return OptimisationResult.Success(request.RequestId, request.SourcePath);
        }
    }
}
