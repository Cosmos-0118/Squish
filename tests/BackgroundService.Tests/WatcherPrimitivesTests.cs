using Squish.BackgroundService;
using Squish.BackgroundService.Automation;
using Squish.Core.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BackgroundService.Tests;

public sealed class WatchPlanTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "plan-tests");

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    [Fact]
    public void PausedYieldsNothing()
    {
        var plan = WatchPlan.Build(true, true, true, new[] { P("a") }, new[] { P("a") });
        Assert.Empty(plan);
    }

    [Fact]
    public void SameFolderForBothKindsSharesOneWatch()
    {
        var plan = WatchPlan.Build(false, true, true, new[] { P("a") }, new[] { P("a") + Path.DirectorySeparatorChar });

        var spec = Assert.Single(plan);
        Assert.Equal(WatchTypes.Image | WatchTypes.Video, spec.Types);
    }

    [Fact]
    public void NestedFolderCoveredByParentIsDropped()
    {
        var plan = WatchPlan.Build(false, true, false, new[] { P("a"), P("a", "b"), P("c") }, Array.Empty<string>());

        Assert.Equal(new[] { P("a"), P("c") }, plan.Select(p => p.Path).ToArray());
    }

    [Fact]
    public void NestedFolderWithExtraTypesKeepsItsOwnWatch()
    {
        var plan = WatchPlan.Build(false, true, true, new[] { P("a") }, new[] { P("a", "b") });

        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void SiblingWithSharedPrefixIsNotTreatedAsNested()
    {
        var plan = WatchPlan.Build(false, true, false, new[] { P("pics"), P("pics-old") }, Array.Empty<string>());

        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void DisabledKindIsIgnoredAndBlankEntriesSkipped()
    {
        var plan = WatchPlan.Build(false, false, true, new[] { P("a") }, new[] { "", "  ", P("v") });

        var spec = Assert.Single(plan);
        Assert.Equal(WatchTypes.Video, spec.Types);
    }
}

public sealed class MediaExtensionFilterTests
{
    [Theory]
    [InlineData(@"C:\x\photo.JPG", 1)]
    [InlineData(@"C:\x\clip.mp4", 2)]
    [InlineData(@"C:\x\doc.pdf", 0)]
    [InlineData(@"C:\x\archive.crdownload", 0)]
    [InlineData(@"C:\x\noext", 0)]
    [InlineData(@"C:\x\trailingdot.", 0)]
    public void ClassifiesByExtension(string path, int expected)
    {
        Assert.Equal((WatchTypes)expected, MediaExtensionFilter.Classify(path));
    }
}

public sealed class FileReadinessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("readiness-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
        }
    }

    private string Make(string name, byte[]? content = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content ?? new byte[] { 1, 2, 3, 4 });
        return path;
    }

    [Fact]
    public void MissingFileIsGone()
    {
        Assert.Equal(FileReadyState.Gone, FileReadiness.Probe(Path.Combine(_dir, "nope.jpg"), -1, 0, DateTime.UtcNow, TimeSpan.Zero).State);
    }

    [Fact]
    public void DirectoryIsGone()
    {
        Assert.Equal(FileReadyState.Gone, FileReadiness.Probe(_dir, -1, 0, DateTime.UtcNow, TimeSpan.Zero).State);
    }

    [Fact]
    public void EmptyFileIsEmpty()
    {
        var path = Make("empty.jpg", Array.Empty<byte>());
        Assert.Equal(FileReadyState.Empty, FileReadiness.Probe(path, -1, 0, DateTime.UtcNow, TimeSpan.Zero).State);
    }

    [Fact]
    public void OldUnlockedFileIsReadyOnFirstLook()
    {
        var path = Make("old.jpg");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(FileReadyState.Ready, FileReadiness.Probe(path, -1, 0, DateTime.UtcNow, TimeSpan.FromMilliseconds(250)).State);
    }

    [Fact]
    public void FreshFileIsUnstableUntilASecondIdenticalLook()
    {
        var path = Make("fresh.jpg");
        var now = DateTime.UtcNow;

        var first = FileReadiness.Probe(path, -1, 0, now, TimeSpan.FromMinutes(10));
        Assert.Equal(FileReadyState.Unstable, first.State);

        var second = FileReadiness.Probe(path, first.Length, first.LastWriteTicks, now, TimeSpan.FromMinutes(10));
        Assert.Equal(FileReadyState.Ready, second.State);
    }

    [Fact]
    public void FileOpenForWritingIsBusyEvenWhenOld()
    {
        var path = Make("writing.jpg");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        Assert.Equal(FileReadyState.Busy, FileReadiness.Probe(path, -1, 0, DateTime.UtcNow, TimeSpan.FromMilliseconds(250)).State);
    }

    [Fact]
    public void FileOpenForReadingOnlyIsStillReady()
    {
        var path = Make("reading.jpg");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(FileReadyState.Ready, FileReadiness.Probe(path, -1, 0, DateTime.UtcNow, TimeSpan.FromMilliseconds(250)).State);
    }

    [Fact]
    public void FutureTimestampNeverCountsAsSettled()
    {
        var path = Make("future.jpg");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(2));
        Assert.Equal(FileReadyState.Unstable, FileReadiness.Probe(path, -1, 0, DateTime.UtcNow, TimeSpan.FromMilliseconds(250)).State);
    }
}

public sealed class OptimisedFileRegistryTests
{
    [Fact]
    public void PathExpiresAfterWindow()
    {
        var registry = new OptimisedFileRegistry(TimeSpan.FromMilliseconds(80));
        var path = FilePath.From(Path.Combine(Path.GetTempPath(), "x.jpg"));
        registry.RegisterPath(path);

        Assert.True(registry.WasPathRecentlyOptimised(path));
        Thread.Sleep(150);
        Assert.False(registry.WasPathRecentlyOptimised(path));
    }

    [Fact]
    public void FingerprintLengthPrecheckAvoidsHashingUnrelatedFiles()
    {
        var registry = new OptimisedFileRegistry();
        Assert.False(registry.MayMatchFingerprintLength(1234));

        registry.RegisterFingerprint(new FileFingerprint(1234, "ABCDEF"));

        Assert.True(registry.MayMatchFingerprintLength(1234));
        Assert.False(registry.MayMatchFingerprintLength(999));
        Assert.True(registry.WasFingerprintRecentlyOptimised(new FileFingerprint(1234, "ABCDEF")));
    }

    [Fact]
    public void CleanupRemovesExpiredEntries()
    {
        var registry = new OptimisedFileRegistry(TimeSpan.FromMilliseconds(30));
        for (var i = 0; i < 100; i++)
        {
            registry.RegisterPath(FilePath.From(Path.Combine(Path.GetTempPath(), $"f{i}.jpg")));
        }

        Thread.Sleep(80);
        // Cleanup is throttled; the first call after construction is always allowed.
        registry.Cleanup();
        Assert.Equal(0, registry.TrackedPathCount);
    }
}

public sealed class ServiceSupervisorTests
{
    private static readonly SupervisorOptions Fast = new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(10),
        MaxDelay = TimeSpan.FromMilliseconds(40),
        StableAfter = TimeSpan.FromMinutes(5)
    };

    [Fact]
    public async Task RestartsAfterExceptionAndKeepsRunning()
    {
        var runs = 0;
        using var cts = new CancellationTokenSource();

        var supervisor = ServiceSupervisor.RunAsync("t", async ct =>
        {
            if (Interlocked.Increment(ref runs) < 4)
            {
                throw new InvalidOperationException("boom");
            }

            await Task.Delay(Timeout.Infinite, ct);
        }, NullLogger.Instance, cts.Token, Fast);

        SpinWait.SpinUntil(() => Volatile.Read(ref runs) >= 4, TimeSpan.FromSeconds(5));
        Assert.Equal(4, Volatile.Read(ref runs));
        Assert.False(supervisor.IsCompleted);

        cts.Cancel();
        await supervisor.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RestartsWhenServiceReturnsWhileHostIsStillRunning()
    {
        var runs = 0;
        using var cts = new CancellationTokenSource();

        var supervisor = ServiceSupervisor.RunAsync("t", _ =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        }, NullLogger.Instance, cts.Token, Fast);

        SpinWait.SpinUntil(() => Volatile.Read(ref runs) >= 3, TimeSpan.FromSeconds(5));
        cts.Cancel();
        await supervisor.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(runs >= 3);
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsPromptly()
    {
        using var cts = new CancellationTokenSource();
        var options = Fast with { InitialDelay = TimeSpan.FromMinutes(10), MaxDelay = TimeSpan.FromMinutes(10) };

        var supervisor = ServiceSupervisor.RunAsync("t", _ => throw new InvalidOperationException("x"), NullLogger.Instance, cts.Token, options);
        await Task.Delay(100);
        cts.Cancel();

        await supervisor.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void BackoffDoublesAndCaps()
    {
        var options = new SupervisorOptions { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(30) };

        Assert.Equal(TimeSpan.FromSeconds(1), ServiceSupervisor.BackoffFor(1, options));
        Assert.Equal(TimeSpan.FromSeconds(2), ServiceSupervisor.BackoffFor(2, options));
        Assert.Equal(TimeSpan.FromSeconds(8), ServiceSupervisor.BackoffFor(4, options));
        Assert.Equal(TimeSpan.FromSeconds(30), ServiceSupervisor.BackoffFor(50, options));
    }
}
