using System.Runtime;

namespace Squish.Core.Shared;

/// <summary>
/// Returns memory to Windows after a burst of work. The .NET GC is allocation-triggered and a normal gen-2
/// collection keeps freed Large Object Heap regions committed, so after optimising a few large images an idle tray
/// app would otherwise keep gigabytes of dead pixel buffers resident. A blocking, compacting, aggressive collection
/// decommits them.
/// </summary>
internal static class MemoryReclaimer
{
    private static readonly TimeSpan FinalizerWait = TimeSpan.FromSeconds(5);
    private static int _running;

    /// <summary>Managed heap size above which <see cref="ReclaimIfPressured"/> acts.</summary>
    public const long DefaultPressureThresholdBytes = 384L * 1024 * 1024;

    /// <summary>
    /// A lightweight reclaim for the gaps between batches of a long job (many pictures in one PDF, one Office file with
    /// hundreds of images), where waiting for the app to go idle would let dead pixel buffers pile up on the Large Object Heap.
    /// Does nothing while the managed heap is under <paramref name="heapThresholdBytes"/>. Otherwise it runs one blocking,
    /// compacting collection - and deliberately leaves the scratch pools alone, since other jobs may be mid-run and would only
    /// re-allocate what was dropped - unless the heap is still over the line afterwards, in which case the pooled arrays are
    /// what is holding it and they are released too. Safe from any thread; overlapping calls collapse into one.
    /// </summary>
    public static void ReclaimIfPressured(long heapThresholdBytes = DefaultPressureThresholdBytes)
    {
        if (GC.GetTotalMemory(forceFullCollection: false) < heapThresholdBytes)
        {
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            return;
        }

        try
        {
            CompactLargeObjectHeap();
            if (GC.GetTotalMemory(forceFullCollection: false) >= heapThresholdBytes && ScratchPools.RetainedBytes > 0)
            {
                ScratchPools.TrimAll();
                CompactLargeObjectHeap();
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Memory reclaim between batches failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static void CompactLargeObjectHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>Compacts the managed heap and decommits freed regions. Safe to call from any thread; overlapping calls collapse into one.</summary>
    public static void Reclaim()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            return;
        }

        try
        {
            // Pooled scoring planes are live objects as far as the GC is concerned; let go of them first.
            ScratchPools.TrimAll();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            // Finalizable objects (MagickImage, bitmaps, SafeHandles) only reach their finalizer after the first pass;
            // run them, then collect again so the native memory they release and the LOH regions they pinned are returned.
            // Bounded wait: a finalizer stuck on a lock must not wedge this method (and with it every later reclaim).
            if (Task.Run(GC.WaitForPendingFinalizers).Wait(FinalizerWait))
            {
                // CompactOnce resets itself after the first collection, so request it again for the second pass.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            else
            {
                Log.Warning("Idle memory reclaim: finalizers did not finish in time; skipping the second pass.");
            }

            // Finally let Windows page out whatever is now untouched, so Task Manager reflects the idle state.
            WorkingSetTrimmer.Trim();
        }
        catch (Exception ex)
        {
            Log.Warning($"Idle memory reclaim failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
