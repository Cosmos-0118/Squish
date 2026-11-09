using System;
using System.IO;
using ImageMagick;

namespace Squish.Core.Imaging;

/// <summary>
/// Prepares optimised image bytes for the Windows clipboard. GDI+ (System.Drawing.Bitmap) only reads
/// PNG/JPEG/GIF/BMP/TIFF, so WebP, AVIF and HEIC output has to be turned into a PNG before it can be offered
/// as a bitmap.
/// </summary>
public static class ClipboardImageConverter
{
    /// <summary>
    /// Returns bytes GDI+ can decode. Formats GDI+ understands are returned untouched; anything else is
    /// decoded (first frame only) and re-encoded as PNG so transparency survives.
    /// </summary>
    /// <param name="path">Optimised image on disk.</param>
    /// <param name="sourceBytes">The file's bytes.</param>
    /// <param name="converted">True when the returned bytes are a new PNG rather than the file's own bytes.</param>
    public static byte[] ToGdiCompatibleBytes(string path, byte[] sourceBytes, out bool converted)
    {
        converted = false;
        var format = ImageFormatSniffer.Sniff(sourceBytes);
        if (format is SourceFormat.Jpeg or SourceFormat.Png or SourceFormat.Gif or SourceFormat.Bmp or SourceFormat.Tiff)
        {
            return sourceBytes;
        }

        using var decoded = ImageIO.Decode(path, format, autoOrient: true);
        decoded.Image.Format = MagickFormat.Png;
        using var output = new MemoryStream();
        decoded.Image.Write(output);
        converted = true;
        return output.ToArray();
    }
}
