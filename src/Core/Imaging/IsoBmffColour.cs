using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Squish.Core.Imaging;

/// <summary>Colour description (CICP code points, ITU-T H.273) signalled by an AVIF/HEIC "nclx" colour box.</summary>
internal readonly record struct IsoColourInfo(int Primaries, int Transfer, int Matrix);

/// <summary>
/// Reads the "colr" box of an ISO-BMFF image (AVIF, HEIC) without decoding it. These files can describe their colours
/// with CICP code points instead of an ICC profile, and the optimiser re-encodes through 8-bit sRGB, so anything that is
/// not sRGB-like (BT.2020, Display P3, PQ/HLG HDR) would silently change meaning.
/// </summary>
internal static class IsoBmffColour
{
    // The meta box (with iprp/ipco/colr) sits at the front of the file; there is no need to read the pixel data.
    private const int MaxHeaderBytes = 1 << 20;

    public static IsoColourInfo? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, MaxHeaderBytes)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return TryRead(buffer.AsSpan(0, read));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The first "nclx" colour description found under meta/iprp/ipco, or null when the file does not carry one.</summary>
    public static IsoColourInfo? TryRead(ReadOnlySpan<byte> file)
    {
        foreach (var meta in Boxes(file, "meta"))
        {
            // meta is a FullBox: four bytes of version and flags come before its children.
            if (meta.Length < 4)
            {
                continue;
            }

            foreach (var iprp in Boxes(file.Slice(meta.Start + 4, meta.Length - 4), "iprp"))
            {
                var iprpData = file.Slice(meta.Start + 4 + iprp.Start, iprp.Length);
                foreach (var ipco in Boxes(iprpData, "ipco"))
                {
                    var ipcoData = iprpData.Slice(ipco.Start, ipco.Length);
                    foreach (var colr in Boxes(ipcoData, "colr"))
                    {
                        var payload = ipcoData.Slice(colr.Start, colr.Length);
                        if (payload.Length >= 11 && Encoding.ASCII.GetString(payload[..4]) == "nclx")
                        {
                            return new IsoColourInfo(
                                (payload[4] << 8) | payload[5],
                                (payload[6] << 8) | payload[7],
                                (payload[8] << 8) | payload[9]);
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when the code points describe something other than sRGB-like SDR: wide-gamut primaries (BT.2020, DCI-P3,
    /// Display P3) or a non-sRGB transfer function (PQ, HLG, linear). Unspecified values and BT.709/BT.601 count as sRGB.
    /// </summary>
    public static bool IsBeyondSrgb(IsoColourInfo colour)
        => colour.Primaries is not (1 or 2) || colour.Transfer is not (1 or 2 or 6 or 13);

    private readonly record struct BoxRange(int Start, int Length);

    /// <summary>Payload ranges (relative to <paramref name="data"/>) of every box of <paramref name="type"/> at this level.</summary>
    private static List<BoxRange> Boxes(ReadOnlySpan<byte> data, string type)
    {
        var found = new List<BoxRange>();
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            long size = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            if (size < 0)
            {
                size += 1L << 32;
            }

            var headerLength = 8;
            if (size == 1)
            {
                if (offset + 16 > data.Length)
                {
                    break;
                }

                size = 0;
                for (var i = 0; i < 8; i++)
                {
                    size = (size << 8) | data[offset + 8 + i];
                }

                headerLength = 16;
            }
            else if (size == 0)
            {
                size = data.Length - offset;
            }

            if (size < headerLength)
            {
                break;
            }

            // A box cut off by the end of what was read is still walked as far as it goes.
            var end = (int)Math.Min(data.Length, offset + size);
            if (Encoding.ASCII.GetString(data.Slice(offset + 4, 4)) == type)
            {
                found.Add(new BoxRange(offset + headerLength, end - (offset + headerLength)));
            }

            if (end <= offset)
            {
                break;
            }

            offset = end;
        }

        return found;
    }
}
