using System;
using System.Collections.Generic;
using System.IO;

namespace ClopWindows.Core.Imaging;

/// <summary>Metadata payloads that may be carried from a source file into an optimised output.</summary>
internal sealed record MetadataBlobs(byte[]? Exif, byte[]? Icc, byte[]? Xmp, byte[]? Iptc)
{
    public static MetadataBlobs Empty { get; } = new(null, null, null, null);

    public bool IsEmpty => Exif is null && Icc is null && Xmp is null && Iptc is null;
}

/// <summary>
/// Minimal JPEG marker reader/writer used to move EXIF / ICC / XMP / IPTC between files without touching the
/// entropy-coded image data. This is what lets lossless recompression ("jpegtran") and external encoders keep
/// exactly the metadata the user's policy allows.
/// </summary>
internal static class JpegSegments
{
    private static readonly byte[] ExifHeader = { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
    private static readonly byte[] XmpHeader = System.Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] IccHeader = System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0");
    private static readonly byte[] PhotoshopHeader = System.Text.Encoding.ASCII.GetBytes("Photoshop 3.0\0");
    private static readonly byte[] ClopComment = System.Text.Encoding.ASCII.GetBytes("Optimised by Clop");
    private const int MaxSegmentPayload = 65533;

    /// <summary>True when the file carries the comment Clop adds after a lossy re-encode.</summary>
    public static bool HasClopMarker(ReadOnlySpan<byte> jpeg)
    {
        foreach (var segment in Enumerate(jpeg))
        {
            if (segment.Marker == 0xFE && StartsWith(jpeg.Slice(segment.PayloadStart, segment.PayloadLength), ClopComment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads EXIF/XMP/ICC/IPTC payloads (raw, without marker headers) from a JPEG.</summary>
    public static MetadataBlobs Extract(ReadOnlySpan<byte> jpeg)
    {
        byte[]? exif = null, xmp = null, iptc = null;
        var iccChunks = new SortedDictionary<int, byte[]>();

        foreach (var segment in Enumerate(jpeg))
        {
            var payload = jpeg.Slice(segment.PayloadStart, segment.PayloadLength);
            switch (segment.Marker)
            {
                case 0xE1 when StartsWith(payload, ExifHeader) && exif is null:
                    exif = payload[ExifHeader.Length..].ToArray();
                    break;
                case 0xE1 when StartsWith(payload, XmpHeader) && xmp is null:
                    xmp = payload[XmpHeader.Length..].ToArray();
                    break;
                case 0xE2 when StartsWith(payload, IccHeader) && payload.Length > IccHeader.Length + 2:
                    iccChunks[payload[IccHeader.Length]] = payload[(IccHeader.Length + 2)..].ToArray();
                    break;
                case 0xED when StartsWith(payload, PhotoshopHeader) && iptc is null:
                    iptc = payload.ToArray();
                    break;
            }
        }

        byte[]? icc = null;
        if (iccChunks.Count > 0)
        {
            using var ms = new MemoryStream();
            foreach (var chunk in iccChunks.Values)
            {
                ms.Write(chunk);
            }

            icc = ms.ToArray();
        }

        return new MetadataBlobs(exif, icc, xmp, iptc);
    }

    /// <summary>
    /// Returns a copy of <paramref name="jpeg"/> whose metadata segments (EXIF, XMP, ICC, Photoshop/IPTC, comments)
    /// are replaced by <paramref name="blobs"/>. APP0/JFIF and all other markers are preserved.
    /// </summary>
    public static byte[] Rewrite(ReadOnlySpan<byte> jpeg, MetadataBlobs blobs, bool addClopMarker = false)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException("Not a JPEG stream.");
        }

        using var output = new MemoryStream(jpeg.Length + 4096);
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        var inserted = false;
        var scanStart = jpeg.Length;

        foreach (var segment in Enumerate(jpeg))
        {
            if (segment.Marker == 0xDA)
            {
                scanStart = segment.HeaderStart;
                break;
            }

            var payload = jpeg.Slice(segment.PayloadStart, segment.PayloadLength);
            var drop = segment.Marker switch
            {
                0xE1 => StartsWith(payload, ExifHeader) || StartsWith(payload, XmpHeader),
                0xE2 => StartsWith(payload, IccHeader),
                0xED => StartsWith(payload, PhotoshopHeader),
                0xFE => true,
                _ => false
            };

            if (!drop)
            {
                output.Write(jpeg.Slice(segment.HeaderStart, segment.TotalLength));
            }

            // Metadata goes right after the JFIF APP0 so decoders that expect JFIF first stay happy.
            if (!inserted && segment.Marker == 0xE0)
            {
                WriteMetadata(output, blobs, addClopMarker);
                inserted = true;
            }
        }

        if (!inserted)
        {
            // No APP0: metadata must still precede the frame header. Rebuild: SOI + metadata + rest.
            var rest = output.ToArray();
            using var rebuilt = new MemoryStream(rest.Length + 4096);
            rebuilt.Write(rest, 0, 2);
            WriteMetadata(rebuilt, blobs, addClopMarker);
            rebuilt.Write(rest, 2, rest.Length - 2);
            rebuilt.Write(jpeg[scanStart..]);
            return rebuilt.ToArray();
        }

        output.Write(jpeg[scanStart..]);
        return output.ToArray();
    }

    /// <summary>EXIF orientation (1-8) from raw EXIF bytes (TIFF header first), or 1.</summary>
    public static int ReadOrientation(byte[] exif)
    {
        if (exif.Length < 12)
        {
            return 1;
        }

        bool little;
        if (exif[0] == 'I' && exif[1] == 'I')
        {
            little = true;
        }
        else if (exif[0] == 'M' && exif[1] == 'M')
        {
            little = false;
        }
        else
        {
            return 1;
        }

        int U16(int offset) => offset + 2 > exif.Length ? 0 : little ? exif[offset] | (exif[offset + 1] << 8) : (exif[offset] << 8) | exif[offset + 1];
        long U32(int offset) => offset + 4 > exif.Length ? 0 : little
            ? exif[offset] | ((long)exif[offset + 1] << 8) | ((long)exif[offset + 2] << 16) | ((long)exif[offset + 3] << 24)
            : ((long)exif[offset] << 24) | ((long)exif[offset + 1] << 16) | ((long)exif[offset + 2] << 8) | exif[offset + 3];

        if (U16(2) != 42)
        {
            return 1;
        }

        var ifd = U32(4);
        if (ifd <= 0 || ifd + 2 > exif.Length)
        {
            return 1;
        }

        var count = U16((int)ifd);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > exif.Length)
            {
                break;
            }

            if (U16(entry) == 0x0112)
            {
                var value = U16(entry + 8);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }

        return 1;
    }

    /// <summary>Builds a tiny EXIF block holding only the orientation tag (used to keep lossless rotations intact).</summary>
    public static byte[] BuildOrientationOnlyExif(int orientation)
    {
        // II, 42, IFD at 8; one entry: tag 0x0112, SHORT, count 1, value.
        return new byte[]
        {
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
            1, 0,
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
            0, 0, 0, 0
        };
    }

    // ---- internals --------------------------------------------------------------------------------------

    private readonly record struct Segment(byte Marker, int HeaderStart, int PayloadStart, int PayloadLength)
    {
        public int TotalLength => PayloadStart + PayloadLength - HeaderStart;
    }

    private static List<Segment> Enumerate(ReadOnlySpan<byte> data)
    {
        var segments = new List<Segment>();
        var position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                break;
            }

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (marker == 0xD9)
            {
                break;
            }

            var length = (data[position + 2] << 8) | data[position + 3];
            if (length < 2 || position + 2 + length > data.Length)
            {
                break;
            }

            segments.Add(new Segment(marker, position, position + 4, length - 2));
            if (marker == 0xDA)
            {
                break;
            }

            position += 2 + length;
        }

        return segments;
    }

    private static void WriteMetadata(Stream output, MetadataBlobs blobs, bool addClopMarker)
    {
        if (addClopMarker)
        {
            WriteSegment(output, 0xFE, ReadOnlySpan<byte>.Empty, ClopComment);
        }

        if (blobs.Exif is { Length: > 0 } exif)
        {
            WriteSegment(output, 0xE1, ExifHeader, exif);
        }

        if (blobs.Xmp is { Length: > 0 } xmp)
        {
            WriteSegment(output, 0xE1, XmpHeader, xmp);
        }

        if (blobs.Icc is { Length: > 0 } icc)
        {
            var chunkSize = MaxSegmentPayload - IccHeader.Length - 2;
            var chunks = (icc.Length + chunkSize - 1) / chunkSize;
            for (var i = 0; i < chunks; i++)
            {
                var offset = i * chunkSize;
                var length = Math.Min(chunkSize, icc.Length - offset);
                var prefix = new byte[IccHeader.Length + 2];
                Buffer.BlockCopy(IccHeader, 0, prefix, 0, IccHeader.Length);
                prefix[IccHeader.Length] = (byte)(i + 1);
                prefix[IccHeader.Length + 1] = (byte)chunks;
                WriteSegment(output, 0xE2, prefix, icc.AsSpan(offset, length));
            }
        }

        if (blobs.Iptc is { Length: > 0 } iptc)
        {
            // Stored with its "Photoshop 3.0" header already attached.
            WriteSegment(output, 0xED, ReadOnlySpan<byte>.Empty, iptc);
        }
    }

    private static void WriteSegment(Stream output, byte marker, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        var total = header.Length + payload.Length;
        if (total > MaxSegmentPayload)
        {
            return; // oversized EXIF/XMP is dropped rather than corrupting the stream
        }

        output.WriteByte(0xFF);
        output.WriteByte(marker);
        var length = total + 2;
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)length);
        output.Write(header);
        output.Write(payload);
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
}
