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
/// Retention is capped - by one byte ceiling shared by every element type (<see cref="ScratchPools.MaxRetainedBytes"/>), so the float
/// and byte pools cannot each hold a full allowance - and <see cref="ScratchPools.TrimAll"/> (called when the app goes idle) hands
/// everything to the GC.
/// </summary>
internal static class ScratchPool<T>
{
    // Arrays below the Large Object Heap threshold are cheap to allocate and collect; pooling them only bloats the table.
    private static readonly int MinimumLength = 85_000 / Unsafe.SizeOf<T>();

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Stack<T[]>> Free = new();

    static ScratchPool() => ScratchPools.Register(typeof(T), EvictAny);

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
                ScratchPools.Release((long)size * Unsafe.SizeOf<T>());
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
        if (TryStore(array, bytes, evictOwnOtherSizes: true))
        {
            return;
        }

        // The shared ceiling is full of the other element types' arrays. Ask them to make room - outside our own lock, since
        // they take theirs, and two pools doing this to each other under their locks could deadlock.
        ScratchPools.EvictFromOthers(typeof(T), bytes);
        TryStore(array, bytes, evictOwnOtherSizes: false);
    }

    private static bool TryStore(T[] array, long bytes, bool evictOwnOtherSizes)
    {
        lock (Gate)
        {
            // Full of other sizes (say from a run of small pictures)? The array being returned is the most recent evidence of
            // what is needed now, so make room by dropping arrays of other classes rather than refusing it.
            if (!ScratchPools.TryReserve(bytes))
            {
                if (evictOwnOtherSizes)
                {
                    EvictOthers(array.Length, bytes);
                }

                if (!ScratchPools.TryReserve(bytes))
                {
                    return false;
                }
            }

            if (!Free.TryGetValue(array.Length, out var stack))
            {
                Free[array.Length] = stack = new Stack<T[]>();
            }

            stack.Push(array);
            return true;
        }
    }

    /// <summary>Drops idle arrays (any size) until about <paramref name="bytesNeeded"/> bytes are freed; returns what was freed.</summary>
    private static long EvictAny(long bytesNeeded)
    {
        var elementBytes = Unsafe.SizeOf<T>();
        long freedTotal = 0;
        lock (Gate)
        {
            foreach (var (size, stack) in Free)
            {
                while (stack.Count > 0 && freedTotal < bytesNeeded)
                {
                    stack.Pop();
                    var freed = (long)size * elementBytes;
                    ScratchPools.Release(freed);
                    freedTotal += freed;
                }

                if (freedTotal >= bytesNeeded)
                {
                    break;
                }
            }
        }

        return freedTotal;
    }

    // Caller holds Gate.
    private static void EvictOthers(int keepSize, long bytesNeeded)
    {
        // Only the part of the request that does not fit under the shared ceiling has to be freed.
        bytesNeeded = Math.Min(bytesNeeded, ScratchPools.RetainedBytes + bytesNeeded - ScratchPools.MaxRetainedBytes);
        if (bytesNeeded <= 0)
        {
            return;
        }

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
                ScratchPools.Release(freed);
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
            foreach (var (size, stack) in Free)
            {
                ScratchPools.Release((long)size * Unsafe.SizeOf<T>() * stack.Count);
            }

            Free.Clear();
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

/// <summary>Coordinates every <see cref="ScratchPool{T}"/>: one shared retention ceiling, and dropping them all.</summary>
internal static class ScratchPools
{
    /// <summary>Most bytes the pools keep idle between them (the float planes and the byte buffers share this one allowance).</summary>
    public const long MaxRetainedBytes = 128L * 1024 * 1024;

    private static long _retainedBytes;

    // Each element type's pool registers a way to drop its idle arrays, so one type returning memory can displace another's.
    private static readonly object EvictorGate = new();
    private static readonly List<(Type Type, Func<long, long> Evict)> Evictors = new();

    internal static void Register(Type type, Func<long, long> evict)
    {
        lock (EvictorGate)
        {
            Evictors.Add((type, evict));
        }
    }

    /// <summary>Frees room for <paramref name="bytes"/> more under the shared ceiling by emptying other element types' idle arrays.</summary>
    internal static void EvictFromOthers(Type requester, long bytes)
    {
        var shortfall = RetainedBytes + bytes - MaxRetainedBytes;
        if (shortfall <= 0)
        {
            return;
        }

        (Type Type, Func<long, long> Evict)[] others;
        lock (EvictorGate)
        {
            others = Evictors.Where(e => e.Type != requester).ToArray();
        }

        foreach (var (_, evict) in others)
        {
            shortfall -= evict(shortfall);
            if (shortfall <= 0)
            {
                return;
            }
        }
    }

    /// <summary>Bytes currently sitting idle in the pools.</summary>
    public static long RetainedBytes => Interlocked.Read(ref _retainedBytes);

    /// <summary>Claims room for <paramref name="bytes"/> under the shared ceiling; false when it would not fit.</summary>
    internal static bool TryReserve(long bytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _retainedBytes);
            if (current + bytes > MaxRetainedBytes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _retainedBytes, current + bytes, current) == current)
            {
                return true;
            }
        }
    }

    internal static void Release(long bytes) => Interlocked.Add(ref _retainedBytes, -bytes);

    public static void TrimAll()
    {
        ScratchPool<float>.Trim();
        ScratchPool<byte>.Trim();
    }
}
