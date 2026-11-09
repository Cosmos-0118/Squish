using System;
using Squish.Core.Shared;

namespace Squish.Core.Imaging;

/// <summary>
/// Decoded 8-bit sRGB pixels (non-premultiplied RGBA, tightly packed, row-major). This is the neutral
/// currency between Magick.NET decoding, the content analyser, the perceptual metric and the crop service.
///
/// A raster made with <see cref="Rent"/> (or <c>pooled: true</c> decoding) borrows its buffer from <see cref="ScratchPool{T}"/>:
/// that buffer may be LONGER than <c>Width * Height * 4</c> and holds stale bytes past that point, so read pixels through
/// <see cref="Span"/> (never <see cref="Pixels"/>'s own length) and dispose the raster when done to give the buffer back.
/// Rasters made with the public constructor own an exact-length array and need no disposal.
/// </summary>
internal sealed class RasterImage : IDisposable
{
    private byte[]? _pooledBuffer;

    public RasterImage(int width, int height, byte[] pixels)
        : this(width, height, pixels, pooled: false)
    {
    }

    private RasterImage(int width, int height, byte[] pixels, bool pooled)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        var required = (long)width * height * 4;
        if (pooled ? pixels.LongLength < required : pixels.LongLength != required)
        {
            throw new ArgumentException("Pixel buffer does not match the image dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
        _pooledBuffer = pooled ? pixels : null;
    }

    /// <summary>A raster whose buffer is borrowed from the scratch pool (undefined contents). Dispose it to return the buffer.</summary>
    public static RasterImage Rent(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        var buffer = ScratchPool<byte>.Rent(checked(width * height * 4));
        return new RasterImage(width, height, buffer, pooled: true);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>RGBA, 4 bytes per pixel. For a pooled raster this array can be longer than the image; prefer <see cref="Span"/>.</summary>
    public byte[] Pixels { get; }

    /// <summary>Exactly this image's pixels (<c>Width * Height * 4</c> bytes), whether or not the backing array is pooled.</summary>
    public Span<byte> Span => Pixels.AsSpan(0, checked(Width * Height * 4));

    public long PixelCount => (long)Width * Height;

    public bool IsPooled => _pooledBuffer is not null;

    /// <summary>Returns a pooled buffer to the scratch pool. A no-op for rasters that own an exact array; safe to call twice.</summary>
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _pooledBuffer, null);
        if (buffer is not null)
        {
            ScratchPool<byte>.Return(buffer);
        }
    }

    /// <summary>True when at least one pixel is not fully opaque.</summary>
    public bool HasVisibleAlpha()
    {
        var pixels = Pixels;
        var end = checked(Width * Height * 4);
        for (var i = 3; i < end; i += 4)
        {
            if (pixels[i] != byte.MaxValue)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Copies a rectangle out of the image (clamped to the image bounds).</summary>
    public RasterImage Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - x);
        height = Math.Clamp(height, 1, Height - y);

        var buffer = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, buffer, row * width * 4, width * 4);
        }

        return new RasterImage(width, height, buffer);
    }

    /// <summary>Packed RGB (alpha dropped) copy, as consumed by the PPM/BMP-style staging writers.</summary>
    public byte[] ToRgb()
    {
        var count = Width * Height;
        var rgb = new byte[count * 3];
        for (int i = 0, s = 0, d = 0; i < count; i++, s += 4, d += 3)
        {
            rgb[d] = Pixels[s];
            rgb[d + 1] = Pixels[s + 1];
            rgb[d + 2] = Pixels[s + 2];
        }

        return rgb;
    }
}
