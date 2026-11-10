using System;
using System.Collections.Generic;
using System.Threading;
using Squish.Core.Shared;

namespace Squish.Core.Imaging;

/// <summary>
/// A memory-light perceptual reference for one video frame: only the tile crops chosen for scoring are kept, so
/// dozens of 4K reference frames fit comfortably in memory.
/// </summary>
internal sealed class TileReference : IDisposable
{
    private readonly List<(SampleTile Tile, RasterImage Pixels)> _tiles;

    // Reference side of each tile's SSIMULACRA2 comparison (XYB pyramid, blurred moments): about half the work of a
    // comparison, and the same for every candidate. Built on first use once caching is enabled.
    private Ssimulacra2.PreparedReference?[]? _prepared;
    private bool[]? _notCacheable;

    private TileReference(int width, int height, List<(SampleTile, RasterImage)> tiles)
    {
        Width = width;
        Height = height;
        _tiles = tiles;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Pixels across the kept tiles; a prepared reference costs about 36 bytes per pixel of these.</summary>
    public long TilePixels
    {
        get
        {
            long total = 0;
            foreach (var (_, pixels) in _tiles)
            {
                total += pixels.PixelCount;
            }

            return total;
        }
    }

    /// <summary>
    /// Keeps each tile's prepared reference between scores, so later candidates (the next quality probes) skip that work.
    /// Opt-in because it costs memory; the caller budgets it with <see cref="TilePixels"/>. Dispose releases it.
    /// </summary>
    public void EnableCaching()
    {
        _prepared ??= new Ssimulacra2.PreparedReference?[_tiles.Count];
        _notCacheable ??= new bool[_tiles.Count];
    }

    public static TileReference Create(RasterImage frame, int wholeFramePixels = 600_000, int tileSize = PerceptualReference.DefaultTileSize, int maxTiles = 4)
    {
        List<SampleTile> rectangles;
        if (frame.PixelCount <= wholeFramePixels || frame.Width < tileSize || frame.Height < tileSize)
        {
            rectangles = new List<SampleTile> { new(0, 0, frame.Width, frame.Height) };
        }
        else
        {
            rectangles = PerceptualReference.ChooseTiles(frame, tileSize, maxTiles);
        }

        var tiles = new List<(SampleTile, RasterImage)>(rectangles.Count);
        foreach (var rectangle in rectangles)
        {
            // A pooled frame goes back to the pool once the caller is done with it, so it cannot be kept as the tile.
            var crop = rectangle.Width == frame.Width && rectangle.Height == frame.Height && !frame.IsPooled
                ? frame
                : frame.Crop(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            tiles.Add((rectangle, crop));
        }

        return new TileReference(frame.Width, frame.Height, tiles);
    }

    /// <summary>SSIMULACRA2-based score (higher is better) of the same frame after encoding.</summary>
    public double Score(RasterImage candidate, CancellationToken token = default)
    {
        if (candidate.Width != Width || candidate.Height != Height)
        {
            throw new ArgumentException("Candidate frame dimensions differ from the reference.", nameof(candidate));
        }

        var scores = new double[_tiles.Count];
        for (var i = 0; i < _tiles.Count; i++)
        {
            var (tile, reference) = _tiles[i];
            var whole = tile.Width == candidate.Width && tile.Height == candidate.Height;
            byte[]? rented = null;
            try
            {
                ReadOnlySpan<byte> pixels;
                if (whole)
                {
                    pixels = candidate.Span;
                }
                else
                {
                    // The tile's rows are not contiguous in the frame; copy them into a pooled buffer instead of allocating one.
                    var length = tile.Width * tile.Height * 4;
                    rented = ScratchPool<byte>.Rent(length);
                    CopyRegion(candidate, tile, rented);
                    pixels = rented.AsSpan(0, length);
                }

                scores[i] = ScoreTile(i, reference, pixels, tile, token);
            }
            finally
            {
                if (rented is not null)
                {
                    ScratchPool<byte>.Return(rented);
                }
            }
        }

        return PerceptualReference.Aggregate(scores);
    }

    private double ScoreTile(int index, RasterImage reference, ReadOnlySpan<byte> candidate, SampleTile tile, CancellationToken token)
    {
        if (_prepared is not null && !_notCacheable![index])
        {
            var prepared = _prepared[index];
            if (prepared is null)
            {
                // Decoded video is opaque; anything else takes the general path.
                if (reference.HasVisibleAlpha())
                {
                    _notCacheable[index] = true;
                }
                else
                {
                    prepared = Ssimulacra2.PrepareOpaqueRgba(reference.Span, tile.Width, tile.Height, token, pooled: true);
                    _prepared[index] = prepared;
                }
            }

            if (prepared is not null && !HasTransparency(candidate))
            {
                return prepared.Compare(candidate, token);
            }
        }

        return PerceptualReference.ScorePair(reference.Span, candidate, tile.Width, tile.Height, token);
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

    private static void CopyRegion(RasterImage source, SampleTile tile, byte[] destination)
    {
        var rowBytes = tile.Width * 4;
        for (var row = 0; row < tile.Height; row++)
        {
            Buffer.BlockCopy(source.Pixels, ((tile.Y + row) * source.Width + tile.X) * 4, destination, row * rowBytes, rowBytes);
        }
    }

    /// <summary>Gives back the pooled planes of every prepared tile.</summary>
    public void Dispose()
    {
        if (_prepared is null)
        {
            return;
        }

        for (var i = 0; i < _prepared.Length; i++)
        {
            _prepared[i]?.Dispose();
            _prepared[i] = null;
        }
    }
}
