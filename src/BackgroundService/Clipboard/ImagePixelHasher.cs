using System.Buffers;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Squish.BackgroundService.Clipboard;

/// <summary>Decoded size and alpha-stripped pixel digest of an image: the same picture gives the same value however it was encoded.</summary>
internal readonly record struct ImagePixels(int Width, int Height, string Hash)
{
    public string Key => $"{Width}x{Height}:{Hash}";
}

/// <summary>
/// Hashes the decoded pixels of an image (colour only, alpha ignored). A screenshot that reaches both the clipboard and a
/// watched folder is encoded differently in each place, but its pixels are identical, so this is how the two are matched.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ImagePixelHasher
{
    /// <summary>
    /// Decoding an image only to hash it costs a pixel buffer (4 bytes per pixel). Beyond this size callers fall back to
    /// other signals, so a huge capture never pays for a second full decode.
    /// </summary>
    internal const long MaxPixels = 20_000_000;

    /// <summary>Returns null when the stream is not a decodable image, is larger than <see cref="MaxPixels"/>, or does not match the expected size.</summary>
    public static ImagePixels? TryHash(Stream stream, int expectedWidth = 0, int expectedHeight = 0)
    {
        try
        {
            using var bitmap = new Bitmap(stream);
            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            {
                return null;
            }

            if ((expectedWidth > 0 && width != expectedWidth) || (expectedHeight > 0 && height != expectedHeight))
            {
                return null;
            }

            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var hash = new XxHash128();

                Span<byte> size = stackalloc byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(size, width);
                BinaryPrimitives.WriteInt32LittleEndian(size[4..], height);
                hash.Append(size);

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

                        hash.Append(row.AsSpan(0, rowBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(row);
                }

                return new ImagePixels(width, height, Convert.ToHexString(hash.GetCurrentHash()));
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            // Undecodable or too big for GDI+.
            return null;
        }
    }

    /// <summary>Hashes an image file, or returns null if it cannot be read or decoded. Never blocks the file's writer or deleter.</summary>
    public static ImagePixels? TryHashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryHash(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
