using System;
using System.Threading;
using Squish.Core.Shared;

namespace Squish.App.Services;

/// <summary>
/// Policy for when the tray app hands its untouched pages back to Windows: only when no Squish window is on screen
/// (refaulting a visible UI's pages would cause hitching), and shortly after the last window goes away.
/// </summary>
public static class IdleTrim
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(2);
    private static System.Threading.Timer? _timer;
    private static volatile bool _mainWindowVisible;
    private static volatile bool _hudVisible;

    public static bool MainWindowVisible
    {
        get => _mainWindowVisible;
        set => _mainWindowVisible = value;
    }

    public static bool HudVisible
    {
        get => _hudVisible;
        set => _hudVisible = value;
    }

    /// <summary>Installs the "is a window visible" guard on the shared trimmer. Call once at startup.</summary>
    public static void Configure()
    {
        WorkingSetTrimmer.ShouldSkip = () => _mainWindowVisible || _hudVisible;
    }

    /// <summary>Trims after <paramref name="delay"/> (default 2 s) if nothing is visible by then. Repeated calls restart the wait.</summary>
    public static void Schedule(TimeSpan? delay = null)
    {
        var due = delay ?? DefaultDelay;
        var timer = _timer ??= new System.Threading.Timer(_ => TrimOrRetry(), null, Timeout.Infinite, Timeout.Infinite);
        timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private static void TrimOrRetry()
    {
        if (WorkingSetTrimmer.Trim())
        {
            return;
        }

        // A trim a moment ago rate-limited this one; try again once the interval has passed instead of dropping it.
        var wait = WorkingSetTrimmer.RemainingRateLimit();
        if (wait > TimeSpan.Zero && !_mainWindowVisible && !_hudVisible)
        {
            _timer?.Change(wait + TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
        }
    }
}
