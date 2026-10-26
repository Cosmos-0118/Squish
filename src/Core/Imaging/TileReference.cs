using System;
using System.Collections.Generic;
using System.Threading;

namespace ClopWindows.Core.Imaging;

/// <summary>
/// A memory-light perceptual reference for one video frame: only the tile crops chosen for scoring are kept, so
/// dozens of 4K reference frames fit comfortably in memory.
/// </summary>
internal sealed class TileReference
{
    private readonly List<(SampleTile Tile, RasterImage Pixels)> _tiles;

    private TileReference(int width, int height, List<(SampleTile, RasterImage)> tiles)
    {
        Width = width;
        Height = height;
        _tiles = tiles;
    }

    public int Width { get; }

    public int Height { get; }

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
            var crop = rectangle.Width == frame.Width && rectangle.Height == frame.Height
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
            var crop = tile.Width == candidate.Width && tile.Height == candidate.Height
                ? candidate
                : candidate.Crop(tile.X, tile.Y, tile.Width, tile.Height);
            scores[i] = PerceptualReference.ScorePair(reference, crop, token);
        }

        return PerceptualReference.Aggregate(scores);
    }
}
