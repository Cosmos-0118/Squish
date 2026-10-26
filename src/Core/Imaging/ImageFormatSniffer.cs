using System;
using System.IO;

namespace ClopWindows.Core.Imaging;

internal enum SourceFormat
{
    Unknown,
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    WebP,
    Avif,
    Heic
}

internal static class SourceFormatExtensions
{
    public static string DefaultExtension(this SourceFormat format) => format switch
    {
        SourceFormat.Jpeg => "jpg",
        SourceFormat.Png => "png",
        SourceFormat.Gif => "gif",
        SourceFormat.Bmp => "bmp",
        SourceFormat.Tiff => "tif",
        SourceFormat.WebP => "webp",
        SourceFormat.Avif => "avif",
        SourceFormat.Heic => "heic",
        _ => string.Empty
    };

    public static SourceFormat FromExtension(string? extension)
    {
        return (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" or "jpe" or "jfif" => SourceFormat.Jpeg,
            "png" => SourceFormat.Png,
            "gif" => SourceFormat.Gif,
            "bmp" => SourceFormat.Bmp,
            "tif" or "tiff" => SourceFormat.Tiff,
            "webp" => SourceFormat.WebP,
            "avif" => SourceFormat.Avif,
            "heic" or "heif" => SourceFormat.Heic,
            _ => SourceFormat.Unknown
        };
    }
}

/// <summary>
/// Identifies an image by its magic bytes, never by its file name. ImageMagick will happily interpret
/// "coders" such as MSL, MVG, URL or HTTP (historically a rich source of vulnerabilities), so every decode
/// is pinned to one of the formats listed here.
/// </summary>
internal static class ImageFormatSniffer
{
    public static SourceFormat Sniff(string path)
    {
        Span<byte> header = stackalloc byte[32];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = stream.Read(header);
        return Sniff(header[..read]);
    }

    public static SourceFormat Sniff(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return SourceFormat.Jpeg;
        }

        if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return SourceFormat.Png;
        }

        if (header.Length >= 6 && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8'
            && (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a')
        {
            return SourceFormat.Gif;
        }

        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
        {
            return SourceFormat.Bmp;
        }

        if (header.Length >= 4
            && ((header[0] == (byte)'I' && header[1] == (byte)'I' && (header[2] == 42 || header[2] == 43) && header[3] == 0)
                || (header[0] == (byte)'M' && header[1] == (byte)'M' && header[2] == 0 && (header[3] == 42 || header[3] == 43))))
        {
            return SourceFormat.Tiff;
        }

        if (header.Length >= 12 && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return SourceFormat.WebP;
        }

        if (header.Length >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            var brand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
            return brand switch
            {
                "avif" or "avis" => SourceFormat.Avif,
                "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" => SourceFormat.Heic,
                _ => SourceFormat.Unknown
            };
        }

        return SourceFormat.Unknown;
    }
}
