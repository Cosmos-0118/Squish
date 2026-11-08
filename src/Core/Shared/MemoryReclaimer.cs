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
    private static int _running;

    /// <summary>Compacts the managed heap and decommits freed regions. Safe to call from any thread; overlapping calls collapse into one.</summary>
    public static void Reclaim()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            return;
        }

        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            // Finalizable objects (MagickImage, bitmaps, SafeHandles) only reach their finalizer after the first pass;
            // run them, then collect again so the native memory they release and the LOH regions they pinned are returned.
            GC.WaitForPendingFinalizers();

            // CompactOnce resets itself after the first collection, so request it again for the second pass.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

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
