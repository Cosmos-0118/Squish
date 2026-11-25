using System;
using System.Buffers;
using System.IO;
using System.IO.Hashing;

namespace Squish.Core.Shared;

/// <summary>
/// Cheap content identity: length plus a 128-bit XxHash3 digest. Files up to <see cref="FullHashLimit"/> are hashed whole;
/// larger ones (videos, big PDFs) are hashed from their length and three 4 KB samples (head, middle, tail), so the cost is
/// constant however large the file is. Bytes and files produce identical values, so clipboard data can be compared to a
/// file on disk.
/// </summary>
public readonly record struct FileFingerprint(long Length, string Hash)
{
    public const int FullHashLimit = 1024 * 1024;
    public const int SampleSize = 4096;

    public bool IsEmpty => string.IsNullOrEmpty(Hash);

    public string Key => string.IsNullOrEmpty(Hash) ? string.Empty : $"{Length}:{Hash}";

    public static FileFingerprint FromBytes(ReadOnlySpan<byte> data)
    {
        var hasher = new XxHash128();
        if (data.Length <= FullHashLimit)
        {
            hasher.Append(data);
        }
        else
        {
            AppendLength(ref hasher, data.Length);
            hasher.Append(data[..SampleSize]);
            hasher.Append(data.Slice((data.Length - SampleSize) / 2, SampleSize));
            hasher.Append(data[^SampleSize..]);
        }

        return new FileFingerprint(data.Length, Convert.ToHexString(hasher.GetCurrentHash()));
    }

    public static bool TryCreate(FilePath path, out FileFingerprint fingerprint)
    {
        fingerprint = default;

        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
            var length = stream.Length;
            var hasher = new XxHash128();

            if (length <= FullHashLimit)
            {
                var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(length, 1));
                try
                {
                    var read = ReadFully(stream, buffer.AsSpan(0, (int)length));
                    if (read != length)
                    {
                        return false;
                    }

                    hasher.Append(buffer.AsSpan(0, read));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                Span<byte> sample = stackalloc byte[SampleSize];
                AppendLength(ref hasher, length);
                foreach (var offset in new[] { 0L, (length - SampleSize) / 2, length - SampleSize })
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    if (ReadFully(stream, sample) != SampleSize)
                    {
                        return false;
                    }

                    hasher.Append(sample);
                }
            }

            fingerprint = new FileFingerprint(length, Convert.ToHexString(hasher.GetCurrentHash()));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int ReadFully(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var n = stream.Read(destination[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static void AppendLength(ref XxHash128 hasher, long length)
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, length);
        hasher.Append(bytes);
    }
}
