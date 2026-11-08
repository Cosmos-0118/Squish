using System.Diagnostics;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

// The trimmer keeps process-wide state (rate limit, ShouldSkip) that MemoryReclaimer also touches from timers in other
// tests, so these tests assert on the pure parts and never on whether a particular Trim() call won the rate limit.
[Collection("WorkingSetTrimmer")]
public sealed class WorkingSetTrimmerTests : IDisposable
{
    public void Dispose() => WorkingSetTrimmer.ShouldSkip = null;

    [Fact]
    public void RateLimitWindowIsTenSeconds()
    {
        var now = Stopwatch.GetTimestamp();
        Assert.False(WorkingSetTrimmer.IsWithinInterval(0, now));
        Assert.True(WorkingSetTrimmer.IsWithinInterval(now - Stopwatch.Frequency * 9, now));
        Assert.False(WorkingSetTrimmer.IsWithinInterval(now - Stopwatch.Frequency * 11, now));
    }

    [Fact]
    public void TrimDoesNothingWhileAWindowIsVisible()
    {
        WorkingSetTrimmer.ShouldSkip = () => true;
        Assert.False(WorkingSetTrimmer.Trim());
    }

    [Fact]
    public void TrimAndReclaimNeverThrow()
    {
        WorkingSetTrimmer.ShouldSkip = null;
        _ = WorkingSetTrimmer.Trim();
        MemoryReclaimer.Reclaim();
        Assert.True(WorkingSetTrimmer.RemainingRateLimit() >= TimeSpan.Zero);
    }
}
