using System.Numerics;
using System.Runtime.CompilerServices;

namespace Squish.Core.Shared;

/// <summary>
/// Reuses large scratch arrays. The perceptual metric needs dozens of multi-megabyte planes per tile; allocating them fresh
/// put every one on the Large Object Heap, which is only reclaimed by blocking gen-2 collections and kept the working set
/// at gigabytes for a single picture.
///
/// Lengths are rounded up to a size class (four steps per doubling, so at most 25% slack): neighbouring tiles of one frame
/// differ by a few pixels in each direction, and every pyramid level halves them again, so exact-length reuse almost never hit.
/// A rented array is therefore LONGER than asked for, and is NOT cleared: callers must index by their own dimensions and
/// write every element they later read.
///
/// Retention is capped, and <see cref="ScratchPools.TrimAll"/> (called when the app goes idle) hands everything to the GC.
/// </summary>
internal static class ScratchPool<T>
{
    // Arrays below the Large Object Heap threshold are cheap to allocate and collect; pooling them only bloats the table.
    private static readonly int MinimumLength = 85_000 / Unsafe.SizeOf<T>();
    private const long MaxRetainedBytes = 160L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Stack<T[]>> Free = new();
    private static long _retainedBytes;

    /// <summary>An array of at least <paramref name="length"/> elements with undefined contents.</summary>
    public static T[] Rent(int length)
    {
        if (length < MinimumLength)
        {
            return new T[length];
        }

        var size = SizeClass(length);
        lock (Gate)
        {
            if (Free.TryGetValue(size, out var stack) && stack.Count > 0)
            {
                _retainedBytes -= (long)size * Unsafe.SizeOf<T>();
                return stack.Pop();
            }
        }

        return new T[size];
    }

    public static void Return(T[]? array)
    {
        if (array is null || array.Length < MinimumLength)
        {
            return;
        }

        var bytes = (long)array.Length * Unsafe.SizeOf<T>();
        lock (Gate)
        {
            // Full of other sizes (say from a run of small pictures)? The array being returned is the most recent evidence of
            // what is needed now, so make room by dropping arrays of other classes rather than refusing it.
            if (_retainedBytes + bytes > MaxRetainedBytes)
            {
                EvictOthers(array.Length, _retainedBytes + bytes - MaxRetainedBytes);
                if (_retainedBytes + bytes > MaxRetainedBytes)
                {
                    return;
                }
            }

            if (!Free.TryGetValue(array.Length, out var stack))
            {
                Free[array.Length] = stack = new Stack<T[]>();
            }

            stack.Push(array);
            _retainedBytes += bytes;
        }
    }

    // Caller holds Gate.
    private static void EvictOthers(int keepSize, long bytesNeeded)
    {
        var elementBytes = Unsafe.SizeOf<T>();
        foreach (var (size, stack) in Free)
        {
            if (size == keepSize)
            {
                continue;
            }

            while (stack.Count > 0 && bytesNeeded > 0)
            {
                stack.Pop();
                var freed = (long)size * elementBytes;
                _retainedBytes -= freed;
                bytesNeeded -= freed;
            }

            if (bytesNeeded <= 0)
            {
                return;
            }
        }
    }

    public static void Trim()
    {
        lock (Gate)
        {
            Free.Clear();
            _retainedBytes = 0;
        }
    }

    /// <summary>Smallest size class (a multiple of a quarter of the enclosing power of two) that holds <paramref name="length"/>.</summary>
    internal static int SizeClass(int length)
    {
        var top = (int)BitOperations.RoundUpToPowerOf2((uint)length) >> 1;   // largest power of two below length (or equal)
        if (top < 4)
        {
            return length;
        }

        var step = top >> 2;
        var rounded = (long)(length + step - 1) / step * step;
        return rounded > int.MaxValue ? length : (int)rounded;
    }
}

/// <summary>Drops every pooled scratch array (see <see cref="ScratchPool{T}"/>).</summary>
internal static class ScratchPools
{
    public static void TrimAll()
    {
        ScratchPool<float>.Trim();
        ScratchPool<byte>.Trim();
    }
}
