using System;

namespace ClopWindows.Core.Imaging;

/// <summary>
/// Decoded 8-bit sRGB pixels (non-premultiplied RGBA, tightly packed, row-major). This is the neutral
/// currency between Magick.NET decoding, the content analyser, the perceptual metric and the crop service.
/// </summary>
internal sealed class RasterImage
{
    public RasterImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        if (pixels.LongLength != (long)width * height * 4)
        {
            throw new ArgumentException("Pixel buffer does not match the image dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>RGBA, 4 bytes per pixel.</summary>
    public byte[] Pixels { get; }

    public long PixelCount => (long)Width * Height;

    /// <summary>True when at least one pixel is not fully opaque.</summary>
    public bool HasVisibleAlpha()
    {
        var pixels = Pixels;
        for (var i = 3; i < pixels.Length; i += 4)
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
