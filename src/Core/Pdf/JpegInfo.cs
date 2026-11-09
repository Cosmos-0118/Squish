using System;

namespace Squish.Core.Pdf;

/// <summary>The few facts about a JPEG stream that decide whether a PDF image can be re-encoded without changing how it is read.</summary>
internal readonly record struct JpegInfo(int Width, int Height, int Components, int AdobeTransform, bool IsProgressive)
{
    public bool HasAdobeMarker => AdobeTransform >= 0;

    public static JpegInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        var adobe = -1;
        var position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
            {
                position += 2;
                continue;
            }

            var length = (data[position + 2] << 8) | data[position + 3];
            if (length < 2 || position + 2 + length > data.Length)
            {
                return null;
            }

            var body = data.Slice(position + 4, length - 2);
            if (marker == 0xEE && body.Length >= 12 && body[..5].SequenceEqual("Adobe"u8))
            {
                adobe = body[11];
            }

            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC)
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (body.Length < 6)
                {
                    return null;
                }

                var height = (body[1] << 8) | body[2];
                var width = (body[3] << 8) | body[4];
                return new JpegInfo(width, height, body[5], adobe, marker == 0xC2 || marker == 0xC6 || marker == 0xCA || marker == 0xCE);
            }

            position += 2 + length;
        }

        return null;
    }
}
