using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Squish.Core.Shared;

/// <summary>
/// Asks Windows to page out the pages this process is not touching, which is what Task Manager's "Memory" column
/// reports. It does not free committed memory (see <see cref="MemoryReclaimer"/> for that): pages are faulted
/// back in on use, so it is only worth doing when the app is idle and nothing is on screen.
/// </summary>
public static class WorkingSetTrimmer
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);

    private static long _lastTrimTimestamp;

    /// <summary>
    /// When set and returning true, <see cref="Trim"/> does nothing. The app uses this to avoid trimming while a
    /// window is visible, where refaulting the UI's pages would cause hitching.
    /// </summary>
    public static Func<bool>? ShouldSkip { get; set; }

    /// <summary>Trims the working set. Safe from any thread; rate limited. Returns true if a trim was requested.</summary>
    public static bool Trim()
    {
        try
        {
            if (ShouldSkip?.Invoke() == true)
            {
                return false;
            }

            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastTrimTimestamp);
            if (IsWithinInterval(last, now))
            {
                return false;
            }

            // Claim the slot; if another thread just did, let it run.
            if (Interlocked.CompareExchange(ref _lastTrimTimestamp, now, last) != last)
            {
                return false;
            }

            // (SIZE_T)-1 for both limits tells Windows to remove as many pages as it can.
            if (!NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), new nint(-1), new nint(-1)))
            {
                Log.Warning($"Working set trim failed (error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Working set trim failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>How long until the next trim is allowed; zero when one is allowed now. Lets callers reschedule a rate-limited request.</summary>
    public static TimeSpan RemainingRateLimit()
    {
        var last = Interlocked.Read(ref _lastTrimTimestamp);
        if (last == 0)
        {
            return TimeSpan.Zero;
        }

        var remaining = MinimumInterval - Stopwatch.GetElapsedTime(last, Stopwatch.GetTimestamp());
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal static bool IsWithinInterval(long lastTimestamp, long nowTimestamp)
        => lastTimestamp != 0 && Stopwatch.GetElapsedTime(lastTimestamp, nowTimestamp) < MinimumInterval;

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll")]
        public static extern nint GetCurrentProcess();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);
    }
}
