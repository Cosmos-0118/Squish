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
