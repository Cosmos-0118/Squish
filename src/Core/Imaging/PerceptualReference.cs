using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Shared;

namespace Squish.Core.Imaging;

/// <summary>A rectangular region of the reference image that is representative enough to judge quality on.</summary>
internal readonly record struct SampleTile(int X, int Y, int Width, int Height);

/// <summary>
/// Pre-computed reference for repeatedly judging encoder candidates with SSIMULACRA2.
/// Small images are compared whole; large images are judged on a handful of tiles chosen for texture and
/// smooth gradients (the places where blocking, ringing and banding show up first) which keeps every
/// comparison in the tens of milliseconds regardless of megapixels.
/// </summary>
internal sealed class PerceptualReference : IDisposable
{
    public const int DefaultFullFramePixels = 1_500_000;
    public const int DefaultTileSize = 384;
    public const int DefaultMaxTiles = 6;

    /// <summary>
    /// Largest full-coverage reference (pixels) whose prepared pyramid is kept for the life of the reference
    /// (~36 bytes per pixel, so ~150 MB). Beyond this each score re-prepares its tiles to bound memory.
    /// </summary>
    public const int MaxCachedFullCoveragePixels = 4_200_000;

    private readonly RasterImage _reference;

    // Reference side of each tile's SSIMULACRA2 comparison, built on first use: [tile, 0] = RGB, [tile, 1] = RGBA.
    private readonly Ssimulacra2.PreparedReference?[,] _prepared;
    private readonly bool?[] _tileTransparency;

    // Full-coverage references would cache the whole image's pyramid (~36 bytes per pixel), so they re-prepare instead.
    private readonly bool _cachePrepared;

    // The stacked sample tiles every parallel search probes; built once, shared (read-only) and dropped with the reference.
    private readonly Lazy<RasterImage> _mosaic;
    private bool _disposed;

    private PerceptualReference(RasterImage reference, IReadOnlyList<SampleTile> tiles, bool wholeImage, bool cachePrepared = true)
    {
        _reference = reference;
        Tiles = tiles;
        IsWholeImage = wholeImage;
        HasAlpha = reference.HasVisibleAlpha();
        _cachePrepared = cachePrepared;
        _prepared = new Ssimulacra2.PreparedReference?[tiles.Count, 2];
        _tileTransparency = new bool?[tiles.Count];
        _mosaic = new Lazy<RasterImage>(() => BuildMosaicCore(pooled: true), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gives back the pooled planes of every prepared tile. Idempotent; the reference must not be scored afterwards.</summary>
    public void Dispose()
    {
        lock (_prepared)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposePrepared();
        }

        if (_mosaic.IsValueCreated)
        {
            _mosaic.Value.Dispose();
        }
    }

    // Caller holds _prepared's lock.
    private void DisposePrepared()
    {
        for (var i = 0; i < _prepared.GetLength(0); i++)
        {
            for (var slot = 0; slot < _prepared.GetLength(1); slot++)
            {
                _prepared[i, slot]?.Dispose();
                _prepared[i, slot] = null;
            }
        }
    }

    public IReadOnlyList<SampleTile> Tiles { get; }

    public bool IsWholeImage { get; }

    public bool HasAlpha { get; }

    public RasterImage Reference => _reference;

    /// <summary>The metric needs at least 8x8 pixels; anything smaller is handled losslessly by callers.</summary>
    public static bool IsSupported(RasterImage image) => image.Width >= Ssimulacra2.MinimumEdge && image.Height >= Ssimulacra2.MinimumEdge;

    public static PerceptualReference Create(RasterImage reference, int fullFramePixels = DefaultFullFramePixels, int tileSize = DefaultTileSize, int maxTiles = DefaultMaxTiles)
    {
        if (!IsSupported(reference))
        {
            throw new ArgumentException("Image is too small for perceptual comparison.", nameof(reference));
        }

        if (reference.PixelCount <= fullFramePixels || reference.Width < tileSize || reference.Height < tileSize)
        {
            var whole = new[] { new SampleTile(0, 0, reference.Width, reference.Height) };
            return new PerceptualReference(reference, whole, wholeImage: true);
        }

        return new PerceptualReference(reference, ChooseTiles(reference, tileSize, maxTiles), wholeImage: false);
    }

    /// <summary>
    /// A reference that judges every pixel: small images whole, larger ones as a grid of tiles covering the full frame,
    /// a couple at a time so memory stays bounded. Needed for palette quantisation, whose banding and colour shifts are
    /// low-frequency: SSIMULACRA2 sees them at its coarse scales, which a small tile cannot represent (a 384-512 px tile
    /// is 12-16 px at the coarsest scale, mostly padding) - small tiles overrated 256-colour screenshots by ~8-10 points
    /// against a whole-image score. 1024 px tiles track the whole-image score.
    /// </summary>
    public static PerceptualReference CreateFullCoverage(RasterImage reference, int fullFramePixels = DefaultFullFramePixels, int tileSize = 1024)
    {
        if (reference.PixelCount <= fullFramePixels || reference.Width < tileSize || reference.Height < tileSize)
        {
            return Create(reference, fullFramePixels: int.MaxValue);
        }

        var tiles = new List<SampleTile>();
        foreach (var (y, height) in Spans(reference.Height, tileSize))
        {
            foreach (var (x, width) in Spans(reference.Width, tileSize))
            {
                tiles.Add(new SampleTile(x, y, width, height));
            }
        }

        // Scoring every candidate against a re-built reference pyramid doubles the work and the allocations, so keep
        // it whenever it fits; only very large frames fall back to re-preparing per score.
        return new PerceptualReference(reference, tiles, wholeImage: false, cachePrepared: reference.PixelCount <= MaxCachedFullCoveragePixels);
    }

    /// <summary>
    /// Splits [0, length) into the fewest near-equal spans that are no longer than <paramref name="size"/>, so a remainder
    /// never inflates one tile (merging it into the last span made a 3840x2560 frame's corner tile 2.75 MP instead of ~1 MP).
    /// </summary>
    internal static IEnumerable<(int Start, int Length)> Spans(int length, int size)
    {
        var count = Math.Max(1, (length + size - 1) / size);
        for (var i = 0; i < count; i++)
        {
            yield return (Boundary(length, count, i), Boundary(length, count, i + 1) - Boundary(length, count, i));
        }
    }

    // Interior boundaries snap to the nearest 16 px so tiles keep JPEG/AVIF block-grid alignment (as ChooseTiles does);
    // a span can therefore exceed the nominal size by a few pixels.
    private static int Boundary(int length, int count, int index)
        => index <= 0 ? 0 : index >= count ? length : Math.Min(length - 1, (int)(((long)length * index / count + 8) / 16 * 16));

    /// <summary>Score of <paramref name="candidate"/> (same dimensions as the reference). Higher is better.</summary>
    public double Score(RasterImage candidate, CancellationToken cancellationToken = default)
    {
        if (candidate.Width != _reference.Width || candidate.Height != _reference.Height)
        {
            throw new ArgumentException("Candidate dimensions differ from the reference.", nameof(candidate));
        }

        var scores = new double[Tiles.Count];
        for (var i = 0; i < Tiles.Count; i++)
        {
            var tile = Tiles[i];
            if (!_cachePrepared)
            {
                // Full coverage of a large frame: one ~1 MP tile at a time. The metric already spreads its blur, downsample
                // and colour passes over every core, so tiles side by side only doubled the working set.
                scores[i] = ScoreUncachedTile(tile, candidate, cancellationToken);
                continue;
            }

            if (tile.Width == candidate.Width && tile.Height == candidate.Height)
            {
                scores[i] = ScoreTile(i, candidate.Span, cancellationToken);
                continue;
            }

            var length = tile.Width * tile.Height * 4;
            var candidateTile = ScratchPool<byte>.Rent(length);
            try
            {
                CopyRegion(candidate, tile, candidateTile);
                scores[i] = ScoreTile(i, candidateTile.AsSpan(0, length), cancellationToken);
            }
            finally
            {
                ScratchPool<byte>.Return(candidateTile);
            }
        }

        return Aggregate(scores);
    }

    /// <summary>
    /// Scores one tile without caching anything: both sides are read through pooled buffers (no per-tile crop allocations)
    /// and the metric borrows its planes from the scratch pool, so scoring a huge frame churns no Large Object Heap memory.
    /// </summary>
    private double ScoreUncachedTile(SampleTile tile, RasterImage candidate, CancellationToken token)
    {
        if (tile.Width == _reference.Width && tile.Height == _reference.Height)
        {
            return ScoreSpans(_reference.Span, candidate.Span, tile.Width, tile.Height, token);
        }

        var length = tile.Width * tile.Height * 4;
        var referenceTile = ScratchPool<byte>.Rent(length);
        var candidateTile = ScratchPool<byte>.Rent(length);
        try
        {
            CopyRegion(_reference, tile, referenceTile);
            CopyRegion(candidate, tile, candidateTile);
            return ScoreSpans(referenceTile.AsSpan(0, length), candidateTile.AsSpan(0, length), tile.Width, tile.Height, token);
        }
        finally
        {
            ScratchPool<byte>.Return(referenceTile);
            ScratchPool<byte>.Return(candidateTile);
        }
    }

    private static void CopyRegion(RasterImage source, SampleTile tile, byte[] destination)
    {
        var rowBytes = tile.Width * 4;
        for (var row = 0; row < tile.Height; row++)
        {
            Buffer.BlockCopy(source.Pixels, ((tile.Y + row) * source.Width + tile.X) * 4, destination, row * rowBytes, rowBytes);
        }
    }

    /// <summary>True when any pixel of the region is not fully opaque (reads the image in place; copies nothing).</summary>
    private static bool RegionHasTransparency(RasterImage source, SampleTile tile)
    {
        if (tile.Width == source.Width && tile.Height == source.Height)
        {
            return HasTransparency(source.Span);
        }

        for (var row = 0; row < tile.Height; row++)
        {
            var start = ((tile.Y + row) * source.Width + tile.X) * 4;
            if (HasTransparency(source.Pixels.AsSpan(start, tile.Width * 4)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scores a candidate that was encoded from a <see cref="BuildMosaic"/> image: tile i of the mosaic is compared
    /// with tile i of the reference.
    /// </summary>
    public double ScoreMosaic(RasterImage decodedMosaic, CancellationToken cancellationToken = default)
    {
        var scores = new double[Tiles.Count];
        var offset = 0;
        for (var i = 0; i < Tiles.Count; i++)
        {
            var tile = Tiles[i];
            if (tile.Width == decodedMosaic.Width)
            {
                // Tiles are stacked full-width, so tile i is already one contiguous run of the decoded mosaic.
                var slice = decodedMosaic.Pixels.AsSpan(offset * decodedMosaic.Width * 4, tile.Width * tile.Height * 4);
                scores[i] = ScoreTile(i, slice, cancellationToken);
            }
            else
            {
                // A narrower (edge-padded) tile: copy just its visible columns.
                var length = tile.Width * tile.Height * 4;
                var candidate = ScratchPool<byte>.Rent(length);
                try
                {
                    CopyRegion(decodedMosaic, new SampleTile(0, offset, tile.Width, tile.Height), candidate);
                    scores[i] = ScoreTile(i, candidate.AsSpan(0, length), cancellationToken);
                }
                finally
                {
                    ScratchPool<byte>.Return(candidate);
                }
            }

            offset += tile.Height;
        }

        return Aggregate(scores);
    }

    /// <summary>
    /// The sample tiles stacked into one small image (see <see cref="BuildMosaic"/>), built on first use and shared by every
    /// search on this reference. Owned by the reference: callers must not dispose or modify it.
    /// </summary>
    public RasterImage Mosaic => _mosaic.Value;

    /// <summary>Stacks the sample tiles into one small image (all tiles share the same width) for fast quality searches.</summary>
    public RasterImage BuildMosaic() => BuildMosaicCore(pooled: false);

    private RasterImage BuildMosaicCore(bool pooled)
    {
        var width = Tiles.Max(t => t.Width);
        var height = Tiles.Sum(t => t.Height);
        var raster = pooled ? RasterImage.Rent(width, height) : new RasterImage(width, height, new byte[width * height * 4]);
        var pixels = raster.Pixels;

        // Pad narrow tiles by edge replication so mosaics stay rectangular.
        var offset = 0;
        foreach (var tile in Tiles)
        {
            for (var row = 0; row < tile.Height; row++)
            {
                var source = ((tile.Y + row) * _reference.Width + tile.X) * 4;
                var destination = ((offset + row) * width) * 4;
                Buffer.BlockCopy(_reference.Pixels, source, pixels, destination, tile.Width * 4);
                for (var x = tile.Width; x < width; x++)
                {
                    Buffer.BlockCopy(_reference.Pixels, source + (tile.Width - 1) * 4, pixels, destination + x * 4, 4);
                }
            }

            offset += tile.Height;
        }

        return raster;
    }

    // The worst region matters most for perceived quality, but a single outlier tile must not dominate.
    internal static double Aggregate(double[] scores)
    {
        if (scores.Length == 1)
        {
            return scores[0];
        }

        Array.Sort(scores);
        var worst = Math.Max(1, (scores.Length + 1) / 2);
        double sum = 0;
        for (var i = 0; i < worst; i++)
        {
            sum += scores[i];
        }

        return sum / worst;
    }

    /// <summary>
    /// Same result as <see cref="ScorePair"/> on tile <paramref name="index"/>, reusing its cached reference side.
    /// <paramref name="candidate"/> holds exactly the tile's pixels (RGBA).
    /// </summary>
    private double ScoreTile(int index, ReadOnlySpan<byte> candidate, CancellationToken token)
    {
        var tile = Tiles[index];
        if (!_cachePrepared)
        {
            return ScoreReferenceTile(tile, candidate, token);
        }

        var candidateAlpha = HasTransparency(candidate);
        Ssimulacra2.PreparedReference prepared;

        // Concurrent searches share one reference; prepare each tile once, then compare without the lock.
        lock (_prepared)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var referenceAlpha = _tileTransparency[index] ??= RegionHasTransparency(_reference, tile);
            var alpha = referenceAlpha || candidateAlpha;
            var slot = alpha ? 1 : 0;
            if (_prepared[index, slot] is not { } cached)
            {
                cached = PrepareTile(tile, alpha, token);
                _prepared[index, slot] = cached;
            }

            // Registered under the same lock that releases prepared tiles, so they cannot go back to the pool mid-comparison.
            prepared = cached;
            prepared.TryEnter();
        }

        // Both layouts read RGBA directly (an opaque reference ignores the alpha byte), so no packed-RGB copy is made.
        try
        {
            return prepared.CompareEntered(candidate, token);
        }
        finally
        {
            prepared.Exit();
        }
    }

    // The reference side of one tile, read straight from the reference (or through a pooled copy of the region) and built
    // from pooled planes, which Dispose() hands back.
    private Ssimulacra2.PreparedReference PrepareTile(SampleTile tile, bool alpha, CancellationToken token)
    {
        if (tile.Width == _reference.Width && tile.Height == _reference.Height)
        {
            return PrepareSpan(_reference.Span, tile, alpha, token);
        }

        var length = tile.Width * tile.Height * 4;
        var region = ScratchPool<byte>.Rent(length);
        try
        {
            CopyRegion(_reference, tile, region);
            return PrepareSpan(region.AsSpan(0, length), tile, alpha, token);
        }
        finally
        {
            ScratchPool<byte>.Return(region);
        }
    }

    private static Ssimulacra2.PreparedReference PrepareSpan(ReadOnlySpan<byte> pixels, SampleTile tile, bool alpha, CancellationToken token)
        => alpha
            ? Ssimulacra2.Prepare(pixels, tile.Width, tile.Height, 4, token, pooled: true)
            : Ssimulacra2.PrepareOpaqueRgba(pixels, tile.Width, tile.Height, token, pooled: true);

    // Uncached path: compare the candidate tile with a pooled copy of the same region of the reference.
    private double ScoreReferenceTile(SampleTile tile, ReadOnlySpan<byte> candidate, CancellationToken token)
    {
        if (tile.Width == _reference.Width && tile.Height == _reference.Height)
        {
            return ScoreSpans(_reference.Span, candidate, tile.Width, tile.Height, token);
        }

        var length = tile.Width * tile.Height * 4;
        var region = ScratchPool<byte>.Rent(length);
        try
        {
            CopyRegion(_reference, tile, region);
            return ScoreSpans(region.AsSpan(0, length), candidate, tile.Width, tile.Height, token);
        }
        finally
        {
            ScratchPool<byte>.Return(region);
        }
    }

    internal static double ScorePair(RasterImage reference, RasterImage candidate, CancellationToken token)
        => ScoreSpans(reference.Span, candidate.Span, reference.Width, reference.Height, token);

    internal static double ScorePair(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> candidate, int width, int height, CancellationToken token)
        => ScoreSpans(reference, candidate, width, height, token);

    private static double ScoreSpans(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> candidate, int width, int height, CancellationToken token)
    {
        var alpha = HasTransparency(reference) || HasTransparency(candidate);
        if (!alpha)
        {
            return Ssimulacra2.ComputeOpaqueRgba(reference, candidate, width, height, token);
        }

        return Ssimulacra2.Compute(reference, candidate, width, height, 4, token);
    }

    private static bool HasTransparency(ReadOnlySpan<byte> rgba)
    {
        for (var i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != byte.MaxValue)
            {
                return true;
            }
        }

        return false;
    }

    internal static List<SampleTile> ChooseTiles(RasterImage image, int tileSize, int maxTiles)
    {
        // Grid of non-overlapping, 16-aligned tiles so JPEG/AVIF block grids line up when tiles are re-assembled.
        var size = tileSize / 16 * 16;
        var columns = image.Width / size;
        var rows = image.Height / size;
        var cells = new List<(int X, int Y, double Activity)>(columns * rows);

        // Centre the grid so borders (often letterboxing / empty margins) are not over-represented.
        var originX = (image.Width - columns * size) / 2 / 16 * 16;
        var originY = (image.Height - rows * size) / 2 / 16 * 16;

        for (var gy = 0; gy < rows; gy++)
        {
            for (var gx = 0; gx < columns; gx++)
            {
                var x = originX + gx * size;
                var y = originY + gy * size;
                cells.Add((x, y, Activity(image, x, y, size)));
            }
        }

        var ordered = cells.OrderByDescending(c => c.Activity).ToList();
        var selected = new List<(int X, int Y, double Activity)>();

        // Most detailed areas first (ringing / blocking), then smooth areas (banding, chroma bleed).
        var textured = Math.Max(1, maxTiles * 2 / 3);
        selected.AddRange(ordered.Take(textured));

        var remaining = ordered.Skip(textured).ToList();
        var smoothSlots = Math.Min(maxTiles - selected.Count, remaining.Count);
        var picked = new SortedSet<int>();
        for (var i = 0; i < smoothSlots; i++)
        {
            // Spread picks over the calmer half of the ranking, median -> smoothest.
            var t = (i + 1.0) / (smoothSlots + 1.0);
            picked.Add((int)((remaining.Count - 1) * (0.5 + 0.5 * t)));
        }

        foreach (var index in picked)
        {
            selected.Add(remaining[index]);
        }

        return selected
            .OrderBy(c => c.Y)
            .ThenBy(c => c.X)
            .Select(c => new SampleTile(c.X, c.Y, size, size))
            .ToList();
    }

    /// <summary>Mean absolute luma gradient over a sparse grid: a cheap proxy for local detail.</summary>
    private static double Activity(RasterImage image, int x0, int y0, int size)
    {
        var pixels = image.Pixels;
        var stride = image.Width * 4;
        double sum = 0;
        var count = 0;
        for (var y = y0; y < y0 + size - 2; y += 3)
        {
            var row = y * stride;
            for (var x = x0; x < x0 + size - 2; x += 3)
            {
                var i = row + x * 4;
                var luma = pixels[i] * 0.299 + pixels[i + 1] * 0.587 + pixels[i + 2] * 0.114;
                var right = pixels[i + 8] * 0.299 + pixels[i + 9] * 0.587 + pixels[i + 10] * 0.114;
                var below = pixels[i + 2 * stride] * 0.299 + pixels[i + 2 * stride + 1] * 0.587 + pixels[i + 2 * stride + 2] * 0.114;
                sum += Math.Abs(luma - right) + Math.Abs(luma - below);
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }
}
