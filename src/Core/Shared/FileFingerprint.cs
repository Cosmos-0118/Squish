using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;

namespace Squish.Core.Shared;

/// <summary>
/// Cheap content identity: length plus a 128-bit XxHash3 digest. Files up to <see cref="FullHashLimit"/> are hashed whole;
/// larger ones (videos, big PDFs) are hashed from their length and three 4 KB samples (head, middle, tail), so the cost is
/// constant however large the file is. Bytes and files produce identical values, so clipboard data can be compared to a
/// file on disk.
/// </summary>
/// <remarks>
/// Hashing is always one-shot on a single buffer: the incremental XxHash128 API is several times slower on large spans.
/// </remarks>
public readonly record struct FileFingerprint(long Length, string Hash)
{
    public const int FullHashLimit = 1024 * 1024;
    public const int SampleSize = 4096;

    private const int SampledBytes = 8 + (3 * SampleSize);

    public bool IsEmpty => string.IsNullOrEmpty(Hash);

    public string Key => string.IsNullOrEmpty(Hash) ? string.Empty : $"{Length}:{Hash}";

    public static FileFingerprint FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length <= FullHashLimit)
        {
            return new FileFingerprint(data.Length, Format(XxHash128.HashToUInt128(data)));
        }

        Span<byte> sampled = stackalloc byte[SampledBytes];
        BinaryPrimitives.WriteInt64LittleEndian(sampled, data.Length);
        data[..SampleSize].CopyTo(sampled[8..]);
        data.Slice((int)SampleOffset(data.Length), SampleSize).CopyTo(sampled[(8 + SampleSize)..]);
        data[^SampleSize..].CopyTo(sampled[(8 + (2 * SampleSize))..]);
        return new FileFingerprint(data.Length, Format(XxHash128.HashToUInt128(sampled)));
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
            // A bare handle and positioned reads: no FileStream buffering or seeking for what is at most four reads.
            using var handle = File.OpenHandle(path.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            var length = RandomAccess.GetLength(handle);

            if (length <= FullHashLimit)
            {
                var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(length, 1));
                try
                {
                    var span = buffer.AsSpan(0, (int)length);
                    if (RandomAccess.Read(handle, span, 0) != length)
                    {
                        return false;
                    }

                    fingerprint = FromBytes(span);
                    return true;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            Span<byte> sampled = stackalloc byte[SampledBytes];
            BinaryPrimitives.WriteInt64LittleEndian(sampled, length);
            if (RandomAccess.Read(handle, sampled.Slice(8, SampleSize), 0) != SampleSize ||
                RandomAccess.Read(handle, sampled.Slice(8 + SampleSize, SampleSize), SampleOffset(length)) != SampleSize ||
                RandomAccess.Read(handle, sampled.Slice(8 + (2 * SampleSize), SampleSize), length - SampleSize) != SampleSize)
            {
                return false;
            }

            fingerprint = new FileFingerprint(length, Format(XxHash128.HashToUInt128(sampled)));
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

    private static long SampleOffset(long length) => (length - SampleSize) / 2;

    // Same text as the earlier streaming implementation (big-endian hex of the 128-bit digest), so markers written by it still match.
    private static string Format(UInt128 hash)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, hash);
        return Convert.ToHexString(bytes);
    }
}
