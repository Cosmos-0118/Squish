using System;
using System.IO;

namespace Squish.Core.Imaging;

/// <summary>
/// Cheap GIF block walk: canvas size and frame count without decoding pixels. ImageMagick caps how many frames it will
/// hold in a list, so long animations (hundreds of frames are common) could not even be probed through it.
/// </summary>
internal sealed record GifInfo(int Width, int Height, int FrameCount)
{
    public static GifInfo? TryRead(string path)
    {
        try
        {
            using var stream = new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), 64 * 1024);
            Span<byte> header = stackalloc byte[13];
            if (stream.Read(header) != 13 || header[0] != (byte)'G' || header[1] != (byte)'I' || header[2] != (byte)'F')
            {
                return null;
            }

            var width = header[6] | (header[7] << 8);
            var height = header[8] | (header[9] << 8);
            if ((header[10] & 0x80) != 0)
            {
                Skip(stream, 3 << ((header[10] & 0x07) + 1));
            }

            var frames = 0;
            while (true)
            {
                var block = stream.ReadByte();
                switch (block)
                {
                    case 0x21: // extension: label, then data sub-blocks
                        if (stream.ReadByte() < 0)
                        {
                            return null;
                        }

                        if (!SkipSubBlocks(stream))
                        {
                            return null;
                        }

                        break;
                    case 0x2C: // image descriptor
                        // Reuse the header buffer (width and height are already read): a stackalloc here would grow the
                        // stack by 9 bytes per frame until the method returns.
                        var descriptor = header[..9];
                        if (stream.Read(descriptor) != 9)
                        {
                            return null;
                        }

                        if ((descriptor[8] & 0x80) != 0)
                        {
                            Skip(stream, 3 << ((descriptor[8] & 0x07) + 1));
                        }

                        if (stream.ReadByte() < 0 || !SkipSubBlocks(stream))
                        {
                            return null;
                        }

                        frames++;
                        break;
                    case 0x3B: // trailer
                        return frames > 0 && width > 0 && height > 0 ? new GifInfo(width, height, frames) : null;
                    default:
                        // End of file without a trailer still leaves every complete frame readable.
                        return block < 0 && frames > 0 && width > 0 && height > 0 ? new GifInfo(width, height, frames) : null;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Skip(Stream stream, int count) => stream.Seek(count, SeekOrigin.Current);

    private static bool SkipSubBlocks(Stream stream)
    {
        while (true)
        {
            var size = stream.ReadByte();
            if (size < 0)
            {
                return false;
            }

            if (size == 0)
            {
                return true;
            }

            Skip(stream, size);
        }
    }
}
