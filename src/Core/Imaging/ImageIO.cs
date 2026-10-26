using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Squish.Core.Shared;
using ImageMagick;

namespace Squish.Core.Imaging;

internal enum ImageColorKind
{
    Rgb,
    Gray,
    Cmyk,
    Other
}

/// <summary>One composited GIF frame and when (in 1/100 s of animation time) it starts.</summary>
internal sealed record GifSample(int Index, long StartTicks, RasterImage Frame);

/// <summary>Everything the optimiser wants to know about a source before (and while) decoding it.</summary>
internal sealed class ImageProbe
{
    public required SourceFormat Format { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Frames / pages (1 for ordinary stills). Animated and multi-page files are left to dedicated paths.</summary>
    public int FrameCount { get; init; } = 1;

    public long PixelCount => (long)Width * Height;
}

/// <summary>A decoded, orientation-normalised image plus the metadata blobs we may need to carry over.</summary>
internal sealed class DecodedImage : IDisposable
{
    public DecodedImage(MagickImage image, SourceFormat format)
    {
        Image = image;
        Format = format;
    }

    public MagickImage Image { get; }

    public SourceFormat Format { get; }

    public int Width => (int)Image.Width;

    public int Height => (int)Image.Height;

    /// <summary>EXIF orientation found in the source (1 when absent). Pixels have already been rotated to match.</summary>
    public int SourceOrientation { get; init; } = 1;

    /// <summary>Estimated JPEG quality (1-100) of the source, or 0 when unknown.</summary>
    public int EstimatedJpegQuality { get; init; }

    /// <summary>True when the source JPEG stores chroma at 4:2:0, false for 4:4:4 / 4:2:2 / other, null when unknown or not a JPEG.</summary>
    public bool? JpegChroma420 { get; init; }

    public int FrameCount { get; init; } = 1;

    public ImageColorKind ColorKind { get; init; } = ImageColorKind.Rgb;

    public byte[]? IccProfile { get; init; }

    public string? IccDescription { get; init; }

    public byte[]? ExifProfile { get; init; }

    public byte[]? XmpProfile { get; init; }

    public byte[]? IptcProfile { get; init; }

    public bool HasGpsData { get; init; }

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Safe image decoding on top of Magick.NET. All reads go through a stream with an explicit, sniffed format so
/// ImageMagick can never be talked into interpreting a file name as a delegate/URL, and global resource limits
/// bound memory, disk and thread use.
/// </summary>
internal static class ImageIO
{
    private static readonly object ConfigureGate = new();
    private static bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured)
        {
            return;
        }

        lock (ConfigureGate)
        {
            if (_configured)
            {
                return;
            }

            try
            {
                ResourceLimits.LimitMemory(new Percentage(40));
                ResourceLimits.Disk = 4UL * 1024 * 1024 * 1024;
                ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount, 1, 4);
                ResourceLimits.Width = 65_536;
                ResourceLimits.Height = 65_536;
                ResourceLimits.Area = 1_000_000_000;
                ResourceLimits.ListLength = 1024;
                ResourceLimits.MaxMemoryRequest = 2UL * 1024 * 1024 * 1024;
            }
            catch (Exception ex)
            {
                Log.Warning($"Unable to apply ImageMagick resource limits: {ex.Message}");
            }

            _configured = true;
        }
    }

    /// <summary>Reads only the header: format sniffed from magic bytes, dimensions from the codec.</summary>
    public static ImageProbe? Probe(string path)
    {
        EnsureConfigured();
        var format = ImageFormatSniffer.Sniff(path);
        if (format == SourceFormat.Unknown)
        {
            return null;
        }

        try
        {
            if (format == SourceFormat.Png && PngInfo.TryRead(path) is { } png)
            {
                return new ImageProbe { Format = format, Width = png.Width, Height = png.Height };
            }

            if (format == SourceFormat.Gif && GifInfo.TryRead(path) is { } gif)
            {
                return new ImageProbe { Format = format, Width = gif.Width, Height = gif.Height, FrameCount = gif.FrameCount };
            }

            using var stream = OpenRead(path);
            var settings = new MagickReadSettings { Format = ToMagickFormat(format) };

            if (format is SourceFormat.Gif or SourceFormat.Tiff or SourceFormat.WebP or SourceFormat.Avif)
            {
                var infos = MagickImageInfo.ReadCollection(stream, settings).ToList();
                if (infos.Count == 0)
                {
                    return null;
                }

                return new ImageProbe { Format = format, Width = (int)infos[0].Width, Height = (int)infos[0].Height, FrameCount = infos.Count };
            }

            var info = new MagickImageInfo();
            info.Read(stream, settings);
            return new ImageProbe { Format = format, Width = (int)info.Width, Height = (int)info.Height };
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>
    /// Composites the leading <paramref name="window"/> frames of a GIF (disposal and transparency applied, as a viewer
    /// would show them) and returns <paramref name="samples"/> of them as flat rasters. With no <paramref name="atTimes"/>
    /// the samples are evenly spaced by frame; with them, each sample is the frame on screen at that animation time, which
    /// lets a re-encoded GIF whose identical neighbouring frames were merged be compared like for like.
    /// </summary>
    public static IReadOnlyList<GifSample> DecodeGifSamples(string path, int window, int samples, IReadOnlyList<long>? atTimes = null)
    {
        EnsureConfigured();
        using var stream = OpenRead(path);
        using var frames = new MagickImageCollection();
        frames.Read(stream, new MagickReadSettings { Format = MagickFormat.Gif, FrameCount = (uint)Math.Max(1, window) });
        if (frames.Count == 0)
        {
            return Array.Empty<GifSample>();
        }

        if (frames.Count > 1)
        {
            frames.Coalesce();
        }

        var starts = new long[frames.Count];
        long clock = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            starts[i] = clock;
            clock += (long)frames[i].AnimationDelay;
        }

        var indices = new List<int>();
        if (atTimes is null)
        {
            var picks = Math.Min(Math.Max(1, samples), frames.Count);
            for (var i = 0; i < picks; i++)
            {
                indices.Add(picks == 1 ? 0 : (int)Math.Round(i * (frames.Count - 1) / (double)(picks - 1)));
            }
        }
        else
        {
            foreach (var time in atTimes)
            {
                var index = Array.FindLastIndex(starts, start => start <= time);
                indices.Add(Math.Max(index, 0));
            }
        }

        return indices.Select(index => new GifSample(index, starts[index], ToRaster((MagickImage)frames[index]))).ToList();
    }

    /// <summary>Decodes the first frame (animated formats are handled by dedicated code paths).</summary>
    public static DecodedImage Decode(string path, SourceFormat format, bool autoOrient)
    {
        EnsureConfigured();
        if (format == SourceFormat.Unknown)
        {
            throw new NotSupportedException("Unsupported or unrecognised image format.");
        }

        var settings = new MagickReadSettings { Format = ToMagickFormat(format) };
        if (format == SourceFormat.Jpeg)
        {
            settings.SetDefine(MagickFormat.Jpeg, "preserve-settings", "false");
        }

        var image = new MagickImage();
        try
        {
            using var stream = OpenRead(path);
            if (format == SourceFormat.Gif)
            {
                using var frames = new MagickImageCollection();
                frames.Read(stream, settings);
                var count = frames.Count;
                if (count == 0)
                {
                    throw new InvalidDataException("Image contains no frames.");
                }

                image.Dispose();
                image = (MagickImage)frames[0].Clone();
                return Finish(image, format, autoOrient, count);
            }

            image.Read(stream, settings);
            return Finish(image, format, autoOrient, 1);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static DecodedImage Finish(MagickImage image, SourceFormat format, bool autoOrient, int frameCount)
    {
        var colorKind = image.ColorSpace switch
        {
            ColorSpace.CMYK => ImageColorKind.Cmyk,
            ColorSpace.Gray or ColorSpace.LinearGray => ImageColorKind.Gray,
            ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.Undefined => ImageColorKind.Rgb,
            _ => ImageColorKind.Other
        };

        var orientation = image.Orientation switch
        {
            Orientation.TopRight => 2,
            Orientation.BottomRight => 3,
            Orientation.BottomLeft => 4,
            Orientation.LeftTop => 5,
            Orientation.RightTop => 6,
            Orientation.RightBottom => 7,
            Orientation.LeftBottom => 8,
            _ => 1
        };

        var exif = image.GetExifProfile();
        var icc = image.GetColorProfile();
        var xmp = image.GetXmpProfile();
        var iptc = image.GetIptcProfile();
        var estimatedQuality = format == SourceFormat.Jpeg ? (int)image.Quality : 0;

        var decoded = new DecodedImage(image, format)
        {
            SourceOrientation = orientation,
            EstimatedJpegQuality = estimatedQuality,
            JpegChroma420 = format == SourceFormat.Jpeg ? IsChroma420(image.GetAttribute("jpeg:sampling-factor")) : null,
            FrameCount = frameCount,
            ColorKind = colorKind,
            IccProfile = icc?.ToByteArray(),
            IccDescription = icc?.Description,
            ExifProfile = exif?.ToByteArray(),
            XmpProfile = xmp?.ToByteArray(),
            IptcProfile = iptc?.ToByteArray(),
            HasGpsData = exif?.GetValue(ExifTag.GPSLatitude) is not null || exif?.GetValue(ExifTag.GPSLongitude) is not null
        };

        if (autoOrient && orientation != 1)
        {
            image.AutoOrient();
        }

        return decoded;
    }

    /// <summary>Flattens the decoded image to 8-bit sRGB RGBA for analysis and metrics.</summary>
    /// <summary>Parses libjpeg's "jpeg:sampling-factor" attribute, e.g. "2x2,1x1,1x1" (4:2:0) or "1x1,1x1,1x1" (4:4:4).</summary>
    internal static bool? IsChroma420(string? samplingFactor)
    {
        if (string.IsNullOrWhiteSpace(samplingFactor))
        {
            return null;
        }

        var components = samplingFactor.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (components.Length < 3)
        {
            // Greyscale or unusual layouts: nothing to subsample.
            return false;
        }

        return components[0] == "2x2" && components[1] == "1x1" && components[2] == "1x1";
    }

    public static RasterImage ToRaster(MagickImage image)
    {
        using var clone = (MagickImage)image.Clone();
        return ToRasterInPlace(clone);
    }

    /// <summary>Same as <see cref="ToRaster"/> but is allowed to mutate (and so avoids copying) the supplied image.</summary>
    public static RasterImage ToRasterInPlace(MagickImage image)
    {
        if (image.ColorSpace == ColorSpace.CMYK || image.ColorSpace == ColorSpace.Gray || image.ColorSpace == ColorSpace.LinearGray)
        {
            image.ColorSpace = ColorSpace.sRGB;
        }

        image.Depth = 8;
        if (!image.HasAlpha)
        {
            image.Alpha(AlphaAction.Opaque);
        }
        else
        {
            image.Alpha(AlphaAction.Set);
        }

        var bytes = image.GetPixelsUnsafe().ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidOperationException("Unable to read pixels.");
        return new RasterImage((int)image.Width, (int)image.Height, bytes);
    }

    /// <summary>Decodes an encoded candidate (any supported format) back into pixels.</summary>
    public static RasterImage DecodeToRaster(string path)
    {
        var format = ImageFormatSniffer.Sniff(path);
        using var decoded = Decode(path, format, autoOrient: false);
        return ToRasterInPlace(decoded.Image);
    }

    public static RasterImage RasterFromPixels(int width, int height, byte[] rgba) => new(width, height, rgba);

    /// <summary>Writes an RGBA raster as PNG (used for staging files handed to external encoders).</summary>
    public static void WriteRasterAsPng(RasterImage raster, string destination, bool fast)
    {
        EnsureConfigured();
        var settings = new PixelReadSettings((uint)raster.Width, (uint)raster.Height, StorageType.Char, PixelMapping.RGBA);
        using var image = new MagickImage();
        image.ReadPixels(raster.Pixels, settings);
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", fast ? "1" : "6");
        image.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "all");
        image.Format = MagickFormat.Png;
        image.Write(destination);
    }

    /// <summary>Writes RGB(A) as a binary PPM/PAM-free staging file for cjpeg (24-bit BMP is universally accepted).</summary>
    public static void WriteRasterAsBmp(RasterImage raster, string destination)
    {
        EnsureConfigured();
        var settings = new PixelReadSettings((uint)raster.Width, (uint)raster.Height, StorageType.Char, PixelMapping.RGBA);
        using var image = new MagickImage();
        image.ReadPixels(raster.Pixels, settings);
        image.Alpha(AlphaAction.Off);
        image.Format = MagickFormat.Bmp3;
        image.Write(destination);
    }

    public static MagickFormat ToMagickFormat(SourceFormat format) => format switch
    {
        SourceFormat.Jpeg => MagickFormat.Jpeg,
        SourceFormat.Png => MagickFormat.Png,
        SourceFormat.Gif => MagickFormat.Gif,
        SourceFormat.Bmp => MagickFormat.Bmp,
        SourceFormat.Tiff => MagickFormat.Tiff,
        SourceFormat.WebP => MagickFormat.WebP,
        SourceFormat.Avif => MagickFormat.Avif,
        SourceFormat.Heic => MagickFormat.Heic,
        _ => throw new NotSupportedException($"Unsupported format {format}.")
    };

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
}
