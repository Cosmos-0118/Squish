using System.Buffers;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Squish.Core.Shared;

namespace Squish.BackgroundService.Clipboard;

/// <summary>
/// Identity of a clipboard image that survives re-encoding. One Snipping Tool capture reaches the clipboard several
/// times (first as a bitmap, then as the tool's own PNG), and the PNG bytes differ between those writes even though
/// the pixels are the same, so the encoded-bytes hash alone cannot tell they are one image.
/// </summary>
/// <param name="Length">Size of the encoded image.</param>
/// <param name="ByteHash">SHA-256 of the encoded image, in the same format <see cref="FileFingerprint"/> uses.</param>
/// <param name="Width">Pixel width read from the PNG header, or 0 when unreadable.</param>
/// <param name="Height">Pixel height read from the PNG header, or 0 when unreadable.</param>
/// <param name="PixelHash">
/// SHA-256 of the decoded pixels, or <see langword="null"/> when the image is too large to decode just for hashing
/// or could not be decoded. The alpha channel is left out: a bitmap that went through CF_DIB comes back opaque while
/// the tool's PNG carries alpha, and the two must still match.
/// </param>
[SupportedOSPlatform("windows")]
internal sealed record ClipboardImageSignature(long Length, string ByteHash, int Width, int Height, string? PixelHash)
{
    /// <summary>
    /// Decoding an image only to hash it costs a pixel buffer (4 bytes per pixel). Beyond this size the encoded-bytes
    /// hash is all that is used, so a huge capture never pays for a second full decode.
    /// </summary>
    internal const long MaxPixelHashPixels = 20_000_000;

    private const int PngHeaderLength = 24;

    private static readonly byte[] IhdrTag = "IHDR"u8.ToArray();

    /// <summary>The same key the optimised-file registry uses, so registry lookups work on clipboard bytes directly.</summary>
    public FileFingerprint Fingerprint => new(Length, ByteHash);

    public bool IsSameImageAs(ClipboardImageSignature other)
    {
        if (Length == other.Length && string.Equals(ByteHash, other.ByteHash, StringComparison.Ordinal))
        {
            return true;
        }

        return PixelHash is not null &&
               other.PixelHash is not null &&
               Width == other.Width &&
               Height == other.Height &&
               string.Equals(PixelHash, other.PixelHash, StringComparison.Ordinal);
    }

    public static ClipboardImageSignature Create(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);

        var byteHash = Convert.ToHexString(SHA256.HashData(pngBytes));
        var (width, height) = TryReadPngSize(pngBytes);

        string? pixelHash = null;
        if (width > 0 && height > 0 && (long)width * height <= MaxPixelHashPixels)
        {
            pixelHash = TryHashPixels(pngBytes, width, height);
        }

        return new ClipboardImageSignature(pngBytes.LongLength, byteHash, width, height, pixelHash);
    }

    internal static (int Width, int Height) TryReadPngSize(ReadOnlySpan<byte> png)
    {
        // 8-byte signature, then the IHDR chunk: length (4), "IHDR" (4), width (4), height (4).
        if (png.Length < PngHeaderLength || !png.Slice(12, 4).SequenceEqual(IhdrTag))
        {
            return (0, 0);
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        return width is 0 or > int.MaxValue || height is 0 or > int.MaxValue
            ? (0, 0)
            : ((int)width, (int)height);
    }

    private static string? TryHashPixels(byte[] pngBytes, int width, int height)
    {
        try
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            using var bitmap = new Bitmap(stream);
            if (bitmap.Width != width || bitmap.Height != height)
            {
                return null;
            }

            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                Span<byte> size = stackalloc byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(size, width);
                BinaryPrimitives.WriteInt32LittleEndian(size[4..], height);
                hash.AppendData(size);

                var rowBytes = width * 4;
                var row = ArrayPool<byte>.Shared.Rent(rowBytes);
                try
                {
                    for (var y = 0; y < height; y++)
                    {
                        // Scan0 + y * Stride is correct for bottom-up bitmaps too (negative stride).
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                        for (var i = 3; i < rowBytes; i += 4)
                        {
                            row[i] = 0;
                        }

                        hash.AppendData(row, 0, rowBytes);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(row);
                }

                return Convert.ToHexString(hash.GetHashAndReset());
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            // Undecodable or too big for GDI+: fall back to the encoded-bytes hash alone.
            return null;
        }
    }
}
