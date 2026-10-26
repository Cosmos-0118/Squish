using System;
using System.IO;

namespace Squish.Core.Imaging;

/// <summary>Cheap PNG chunk walk: dimensions, bit depth, animation and colour-profile flags without decoding pixels.</summary>
internal sealed record PngInfo(int Width, int Height, int BitDepth, int ColorType, bool IsAnimated, bool HasIccProfile, bool HasTransparencyChunk)
{
    public bool HasAlphaChannel => ColorType is 4 or 6 || HasTransparencyChunk;

    public bool IsIndexed => ColorType == 3;

    public static PngInfo? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != 8)
            {
                return null;
            }

            int width = 0, height = 0, depth = 0, colorType = 0;
            bool animated = false, icc = false, trns = false;
            Span<byte> chunkHeader = stackalloc byte[8];
            var buffer = new byte[13];

            while (stream.Read(chunkHeader) == 8)
            {
                var length = (uint)((chunkHeader[0] << 24) | (chunkHeader[1] << 16) | (chunkHeader[2] << 8) | chunkHeader[3]);
                var type = System.Text.Encoding.ASCII.GetString(chunkHeader.Slice(4, 4));

                if (type == "IHDR" && length >= 13)
                {
                    if (stream.Read(buffer, 0, 13) != 13)
                    {
                        return null;
                    }

                    width = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
                    height = (buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7];
                    depth = buffer[8];
                    colorType = buffer[9];
                    stream.Seek(length - 13 + 4, SeekOrigin.Current);
                    continue;
                }

                switch (type)
                {
                    case "acTL":
                        animated = true;
                        break;
                    case "iCCP":
                        icc = true;
                        break;
                    case "tRNS":
                        trns = true;
                        break;
                }

                if (type is "IDAT" or "IEND")
                {
                    break;
                }

                stream.Seek((long)length + 4, SeekOrigin.Current);
            }

            return width > 0 && height > 0 ? new PngInfo(width, height, depth, colorType, animated, icc, trns) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
