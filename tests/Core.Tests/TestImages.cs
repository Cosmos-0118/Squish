using System;
using System.IO;
using ClopWindows.Core.Shared;
using ImageMagick;

namespace Core.Tests;

/// <summary>Deterministic synthetic pictures: photo-like fractal texture, flat "UI" graphics, gradients.</summary>
internal static class TestImages
{
    public static byte[] PhotoLikeRgba(int width, int height, int seed = 7, bool alpha = false)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var value = 0.0;
                    var amplitude = 0.5;
                    var frequency = 1.0 / 96.0;
                    for (var octave = 0; octave < 5; octave++)
                    {
                        value += amplitude * ValueNoise(x * frequency, y * frequency, seed * 31 + c * 977 + octave * 131);
                        amplitude *= 0.5;
                        frequency *= 2.1;
                    }

                    // Mild sensor-like grain keeps encoders honest without making the picture incompressible.
                    var grain = (Hash(x, y, seed + c) % 7) - 3;
                    var shaded = value * 200 + 28 + grain + 20 * Math.Sin((x + y) / 140.0);
                    pixels[i + c] = (byte)Math.Clamp(shaded, 0, 255);
                }

                pixels[i + 3] = alpha ? (byte)Math.Clamp(230 - 120 * Math.Abs(Math.Sin(x / 180.0)), 0, 255) : (byte)255;
            }
        }

        return pixels;
    }

    /// <summary>Flat colours, hard edges, text-like stripes: what a screenshot or icon looks like.</summary>
    public static byte[] GraphicRgba(int width, int height, bool transparentBackground = false)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                byte r = 245, g = 246, b = 248, a = 255;
                if (transparentBackground)
                {
                    a = 0;
                }

                if (y < 40)
                {
                    r = 32; g = 33; b = 36; a = 255;
                }
                else if (x < width / 5)
                {
                    r = 236; g = 238; b = 242; a = 255;
                }
                else if (((x / 3 + y / 5) % 7) == 0 && y % 24 < 12)
                {
                    r = 30; g = 30; b = 30; a = 255;
                }
                else if ((x - width * 3 / 5) * (x - width * 3 / 5) + (y - height / 2) * (y - height / 2) < (height / 5) * (height / 5))
                {
                    r = 66; g = 133; b = 244; a = 255;
                }

                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = a;
            }
        }

        return pixels;
    }

    public static FilePath Save(byte[] rgba, int width, int height, string extension, Action<MagickImage>? configure = null)
    {
        var path = FilePath.TempFile("clop-test-image", extension, addUniqueSuffix: true);
        var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA);
        using var image = new MagickImage();
        image.ReadPixels(rgba, settings);
        configure?.Invoke(image);
        image.Write(path.Value);
        return path;
    }

    public static FilePath SaveJpeg(byte[] rgba, int width, int height, int quality, Action<MagickImage>? configure = null)
        => Save(rgba, width, height, "jpg", image =>
        {
            image.Alpha(AlphaAction.Off);
            image.Quality = (uint)quality;
            image.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "false");
            image.Settings.Interlace = Interlace.NoInterlace;
            configure?.Invoke(image);
        });

    public static FilePath SavePng(byte[] rgba, int width, int height, Action<MagickImage>? configure = null)
        => Save(rgba, width, height, "png", image =>
        {
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
            configure?.Invoke(image);
        });

    public static byte[] ReadRgba(string path)
    {
        using var image = new MagickImage(path);
        image.Depth = 8;
        if (!image.HasAlpha)
        {
            image.Alpha(AlphaAction.Opaque);
        }

        return image.GetPixelsUnsafe().ToByteArray(PixelMapping.RGBA)!;
    }

    public static (int Width, int Height) Dimensions(string path)
    {
        var info = new MagickImageInfo(path);
        return ((int)info.Width, (int)info.Height);
    }

    public static void TryDelete(params FilePath?[] paths)
    {
        foreach (var path in paths)
        {
            if (path is null)
            {
                continue;
            }

            try
            {
                if (File.Exists(path.Value.Value))
                {
                    File.Delete(path.Value.Value);
                }
            }
            catch (IOException)
            {
                // ignore cleanup issues
            }
        }
    }

    private static double ValueNoise(double x, double y, int seed)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        double V(int px, int py) => (Hash(px, py, seed) & 0xFFFF) / 65535.0;
        var top = V(x0, y0) * (1 - fx) + V(x0 + 1, y0) * fx;
        var bottom = V(x0, y0 + 1) * (1 - fx) + V(x0 + 1, y0 + 1) * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static int Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            return (int)((h ^ (h >> 16)) & 0x7FFFFFFF);
        }
    }
}
