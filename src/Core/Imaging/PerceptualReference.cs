using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ClopWindows.Core.Imaging;

/// <summary>A rectangular region of the reference image that is representative enough to judge quality on.</summary>
internal readonly record struct SampleTile(int X, int Y, int Width, int Height);

/// <summary>
/// Pre-computed reference for repeatedly judging encoder candidates with SSIMULACRA2.
/// Small images are compared whole; large images are judged on a handful of tiles chosen for texture and
/// smooth gradients (the places where blocking, ringing and banding show up first) which keeps every
/// comparison in the tens of milliseconds regardless of megapixels.
/// </summary>
internal sealed class PerceptualReference
{
    public const int DefaultFullFramePixels = 1_500_000;
    public const int DefaultTileSize = 384;
    public const int DefaultMaxTiles = 6;

    private readonly RasterImage _reference;

    private PerceptualReference(RasterImage reference, IReadOnlyList<SampleTile> tiles, bool wholeImage)
    {
        _reference = reference;
        Tiles = tiles;
        IsWholeImage = wholeImage;
        HasAlpha = reference.HasVisibleAlpha();
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
            scores[i] = ScoreTile(Tiles[i], _reference, candidate, cancellationToken);
        }

        return Aggregate(scores);
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
            var reference = _reference.Crop(tile.X, tile.Y, tile.Width, tile.Height);
            var candidate = decodedMosaic.Crop(0, offset, tile.Width, tile.Height);
            scores[i] = ScorePair(reference, candidate, cancellationToken);
            offset += tile.Height;
        }

        return Aggregate(scores);
    }

    /// <summary>Stacks the sample tiles into one small image (all tiles share the same width) for fast quality searches.</summary>
    public RasterImage BuildMosaic()
    {
        var width = Tiles.Max(t => t.Width);
        var height = Tiles.Sum(t => t.Height);
        var pixels = new byte[width * height * 4];

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

        return new RasterImage(width, height, pixels);
    }

    // The worst region matters most for perceived quality, but a single outlier tile must not dominate.
    private static double Aggregate(double[] scores)
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

    private static double ScoreTile(SampleTile tile, RasterImage reference, RasterImage candidate, CancellationToken token)
    {
        if (tile.Width == reference.Width && tile.Height == reference.Height)
        {
            return ScorePair(reference, candidate, token);
        }

        return ScorePair(reference.Crop(tile.X, tile.Y, tile.Width, tile.Height), candidate.Crop(tile.X, tile.Y, tile.Width, tile.Height), token);
    }

    private static double ScorePair(RasterImage reference, RasterImage candidate, CancellationToken token)
    {
        var alpha = HasTransparency(reference.Pixels) || HasTransparency(candidate.Pixels);
        if (!alpha)
        {
            return Ssimulacra2.Compute(Rgb(reference), Rgb(candidate), reference.Width, reference.Height, 3, token);
        }

        return Ssimulacra2.Compute(reference.Pixels, candidate.Pixels, reference.Width, reference.Height, 4, token);
    }

    private static byte[] Rgb(RasterImage image) => image.ToRgb();

    private static bool HasTransparency(byte[] rgba)
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

    private static List<SampleTile> ChooseTiles(RasterImage image, int tileSize, int maxTiles)
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
