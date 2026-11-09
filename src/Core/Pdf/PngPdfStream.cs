using System;
using System.Buffers.Binary;
using System.IO;

namespace Squish.Core.Pdf;

/// <summary>
/// The pieces of a PNG that a PDF image XObject can reuse as they are. The concatenated IDAT payload of a non-interlaced PNG is a
/// zlib stream of filtered scanlines (one filter-type byte per row), which is exactly what <c>/FlateDecode</c> with
/// <c>/Predictor 15</c> expects - so oxipng's or pngquant's output travels into the PDF without being deflated again.
/// </summary>
internal sealed record PngPdfStream(int Width, int Height, int BitDepth, int ColorType, byte[]? Palette, byte[] Idat)
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Colour components per sample: 1 for greyscale and palette indexes, 3 for RGB.</summary>
    public int Colors => ColorType == 2 ? 3 : 1;

    public int PaletteEntries => Palette is null ? 0 : Palette.Length / 3;

    /// <summary>
    /// Null for anything a PDF cannot express without re-encoding: interlacing, 16-bit samples, alpha channels, a transparency
    /// chunk, or a damaged file.
    /// </summary>
    public static PngPdfStream? TryRead(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length + 12 || !png[..Signature.Length].SequenceEqual(Signature))
        {
            return null;
        }

        int width = 0, height = 0, bitDepth = 0, colorType = -1;
        byte[]? palette = null;
        var idat = new MemoryStream();
        var sawHeader = false;
        var sawEnd = false;

        var position = Signature.Length;
        while (position + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            var type = png.Slice(position + 4, 4);
            if (length > int.MaxValue || position + 12L + length > png.Length)
            {
                return null;
            }

            var data = png.Slice(position + 8, (int)length);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (data.Length != 13 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    return null; // unknown compression/filter method, or interlaced
                }

                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                sawHeader = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (length == 0 || length % 3 != 0 || length > 768)
                {
                    return null;
                }

                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                return null; // per-index or colour-key transparency has no equivalent in an opaque PDF image
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
                break;
            }

            position += 12 + (int)length;
        }

        if (!sawHeader || !sawEnd || width <= 0 || height <= 0 || idat.Length == 0)
        {
            return null;
        }

        var valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8,
            2 => bitDepth == 8,
            3 => bitDepth is 1 or 2 or 4 or 8 && palette is not null && palette.Length / 3 <= (1 << bitDepth),
            _ => false // alpha channels (4, 6) cannot be carried in the colour data
        };

        return valid ? new PngPdfStream(width, height, bitDepth, colorType, colorType == 3 ? palette : null, idat.ToArray()) : null;
    }
}
