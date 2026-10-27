using System.Collections.Concurrent;
using Squish.BackgroundService.Automation;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BackgroundService.Tests;

internal sealed class FakeFolderSettings : IFolderWatchSettings
{
    public FolderWatchConfig Current { get; private set; }

    public event EventHandler? Changed;

    public FakeFolderSettings(string imageDir, string? videoDir = null, int imageJobs = 4)
    {
        Current = new FolderWatchConfig(
            Paused: false,
            AutoImages: true,
            AutoVideos: videoDir is not null,
            ImageDirectories: new[] { imageDir },
            VideoDirectories: videoDir is null ? Array.Empty<string>() : new[] { videoDir },
            SkipImageFormats: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tiff" },
            SkipVideoFormats: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            MaxImageSizeMb: 50,
            MaxVideoSizeMb: 500,
            MaxImageJobs: imageJobs,
            MaxVideoJobs: 1);
    }

    public void Update(Func<FolderWatchConfig, FolderWatchConfig> change)
    {
        Current = change(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyRequestMetadata(ItemType type, FilePath path, IDictionary<string, object?> metadata)
    {
    }
}

internal sealed class RecordingOptimiser : IOptimiser
{
    private int _running;
    private int _peak;

    public RecordingOptimiser(ItemType type = ItemType.Image) => ItemType = type;

    public ItemType ItemType { get; }

    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(5);

    public ConcurrentQueue<string> Processed { get; } = new();

    public int PeakConcurrency => Volatile.Read(ref _peak);

    public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _running);
        int peak;
        while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak)
        {
        }

        try
        {
            await Task.Delay(Delay, cancellationToken);
            Processed.Enqueue(request.SourcePath.Value);
            return OptimisationResult.Success(request.RequestId, request.SourcePath);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

public sealed class DirectoryOptimisationServiceTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("dirwatch-").FullName;
    private readonly string _images;
    private readonly string _videos;
    private readonly RecordingOptimiser _imageOptimiser = new(ItemType.Image);
    private readonly RecordingOptimiser _videoOptimiser = new(ItemType.Video);
    private OptimisationCoordinator _coordinator = null!;
    private CancellationTokenSource _cts = new();
    private Task? _run;

    private static readonly FolderWatchOptions Fast = new()
    {
        ImageQuietPeriod = TimeSpan.FromMilliseconds(60),
        VideoQuietPeriod = TimeSpan.FromMilliseconds(100),
        RenameQuietPeriod = TimeSpan.FromMilliseconds(30),
        BusyBackoffBase = TimeSpan.FromMilliseconds(50),
        BusyBackoffMax = TimeSpan.FromMilliseconds(200),
        HealthInterval = TimeSpan.FromMilliseconds(200),
        PollInterval = TimeSpan.FromHours(1),
        DeferredFallback = TimeSpan.FromSeconds(30)
    };

    public DirectoryOptimisationServiceTests()
    {
        _images = Path.Combine(_root, "images");
        _videos = Path.Combine(_root, "videos");
        Directory.CreateDirectory(_images);
        Directory.CreateDirectory(_videos);
    }

    public Task InitializeAsync()
    {
        _coordinator = new OptimisationCoordinator(new IOptimiser[] { _imageOptimiser, _videoOptimiser }, degreeOfParallelism: 4);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _coordinator.DisposeAsync();
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
        }
    }

    private DirectoryOptimisationService Start(FakeFolderSettings settings, OptimisedFileRegistry? registry = null)
    {
        var service = new DirectoryOptimisationService(_coordinator, registry ?? new OptimisedFileRegistry(), NullLogger<DirectoryOptimisationService>.Instance, settings, Fast, TimeProvider.System);
        _run = Task.Run(() => service.RunAsync(_cts.Token));
        return service;
    }

    private static byte[] Bytes(int n = 2048) => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray();

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 8000, string? message = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), message ?? "Condition was not met in time.");
    }

    [Fact]
    public async Task OptimisesNewImageFile()
    {
        Start(new FakeFolderSettings(_images));
        await Task.Delay(200);

        var path = Path.Combine(_images, "new.png");
        await File.WriteAllBytesAsync(path, Bytes());

        await Eventually(() => _imageOptimiser.Processed.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OptimisesFilesInNewSubdirectories()
    {
        Start(new FakeFolderSettings(_images));
        await Task.Delay(200);

        var sub = Path.Combine(_images, "a", "b");
        Directory.CreateDirectory(sub);
        var path = Path.Combine(sub, "deep.jpg");
        await File.WriteAllBytesAsync(path, Bytes());

        await Eventually(() => _imageOptimiser.Processed.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IgnoresNonMediaSkippedFormatsAndSquishOutputs()
    {
        var service = Start(new FakeFolderSettings(_images));
        await Task.Delay(200);

        await File.WriteAllBytesAsync(Path.Combine(_images, "notes.txt"), Bytes());
        await File.WriteAllBytesAsync(Path.Combine(_images, "scan.tiff"), Bytes());
        await File.WriteAllBytesAsync(Path.Combine(_images, "photo.squish.png"), Bytes());
        var good = Path.Combine(_images, "good.png");
        await File.WriteAllBytesAsync(good, Bytes());

        await Eventually(() => _imageOptimiser.Processed.Contains(good, StringComparer.OrdinalIgnoreCase));
        await Task.Delay(300);

        Assert.Single(_imageOptimiser.Processed);
        Assert.Equal(1, service.Stats.Submitted);
    }

    [Fact]
    public async Task ARenamedDownloadIsPickedUpUnderItsFinalName()
    {
        Start(new FakeFolderSettings(_images));
        await Task.Delay(200);

        var partial = Path.Combine(_images, "download.crdownload");
        await File.WriteAllBytesAsync(partial, Bytes());
        var final = Path.Combine(_images, "download.jpg");
        File.Move(partial, final);

        await Eventually(() => _imageOptimiser.Processed.Contains(final, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AFileStillBeingWrittenDoesNotBlockOtherFilesAndIsProcessedOnceReleased()
    {
        Start(new FakeFolderSettings(_images));
        await Task.Delay(200);

        var busyPath = Path.Combine(_images, "recording.png");
        var writer = new FileStream(busyPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(Bytes(), 0, 1024);
        writer.Flush();

        var others = Enumerable.Range(0, 20).Select(i => Path.Combine(_images, $"other-{i}.png")).ToArray();
        foreach (var other in others)
        {
            await File.WriteAllBytesAsync(other, Bytes());
        }

        // Every other file finishes while the busy one is still held open.
        await Eventually(() => others.All(o => _imageOptimiser.Processed.Contains(o, StringComparer.OrdinalIgnoreCase)), 5000);
        Assert.DoesNotContain(busyPath, _imageOptimiser.Processed, StringComparer.OrdinalIgnoreCase);

        writer.Dispose();
        File.SetLastWriteTimeUtc(busyPath, DateTime.UtcNow.AddMinutes(-1));

        await Eventually(() => _imageOptimiser.Processed.Contains(busyPath, StringComparer.OrdinalIgnoreCase), 8000);
    }

    [Fact]
    public async Task EachFileIsOptimisedExactlyOnceDespiteManyChangeEvents()
    {
        var service = Start(new FakeFolderSettings(_images));
        await Task.Delay(200);

        var path = Path.Combine(_images, "growing.png");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            for (var i = 0; i < 30; i++)
            {
                await stream.WriteAsync(Bytes(512));
                await stream.FlushAsync();
                await Task.Delay(10);
            }
        }

        await Eventually(() => _imageOptimiser.Processed.Count >= 1);
        await Task.Delay(500);

        Assert.Single(_imageOptimiser.Processed);
        Assert.Equal(1, service.Stats.Submitted);
    }

    [Fact]
    public async Task BurstOfFilesIsFullyProcessedWithoutLoss()
    {
        var service = Start(new FakeFolderSettings(_images, imageJobs: 0));
        await Task.Delay(200);

        const int count = 600;
        for (var i = 0; i < count; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(_images, $"burst-{i:D4}.png"), Bytes(256));
        }

        await Eventually(() => _imageOptimiser.Processed.Count >= count, 30_000, $"Only {_imageOptimiser.Processed.Count}/{count} processed (stats {service.Stats}).");
        Assert.Equal(count, _imageOptimiser.Processed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task RespectsMaximumConcurrentJobsAndDrainsTheBacklog()
    {
        _imageOptimiser.Delay = TimeSpan.FromMilliseconds(80);
        Start(new FakeFolderSettings(_images, imageJobs: 2));
        await Task.Delay(200);

        for (var i = 0; i < 12; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(_images, $"limited-{i}.png"), Bytes());
        }

        await Eventually(() => _imageOptimiser.Processed.Count >= 12, 20_000);
        Assert.True(_imageOptimiser.PeakConcurrency <= 2, $"peak concurrency was {_imageOptimiser.PeakConcurrency}");
    }

    [Fact]
    public async Task PauseStopsProcessingAndResumeRestartsIt()
    {
        var settings = new FakeFolderSettings(_images);
        Start(settings);
        await Task.Delay(200);

        settings.Update(c => c with { Paused = true });
        await Task.Delay(200);
        await File.WriteAllBytesAsync(Path.Combine(_images, "while-paused.png"), Bytes());
        await Task.Delay(600);
        Assert.Empty(_imageOptimiser.Processed);

        settings.Update(c => c with { Paused = false });
        await Task.Delay(300);
        var after = Path.Combine(_images, "after-resume.png");
        await File.WriteAllBytesAsync(after, Bytes());

        await Eventually(() => _imageOptimiser.Processed.Contains(after, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(_imageOptimiser.Processed, p => p.EndsWith("while-paused.png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChangingFoldersAtRuntimeMovesTheWatch()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var settings = new FakeFolderSettings(_images);
        Start(settings);
        await Task.Delay(200);

        settings.Update(c => c with { ImageDirectories = new[] { other } });
        await Task.Delay(300);

        await File.WriteAllBytesAsync(Path.Combine(_images, "old-folder.png"), Bytes());
        var moved = Path.Combine(other, "new-folder.png");
        await File.WriteAllBytesAsync(moved, Bytes());

        await Eventually(() => _imageOptimiser.Processed.Contains(moved, StringComparer.OrdinalIgnoreCase));
        await Task.Delay(300);
        Assert.DoesNotContain(_imageOptimiser.Processed, p => p.EndsWith("old-folder.png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WatchRecoversWhenFolderIsDeletedAndRecreated()
    {
        var service = Start(new FakeFolderSettings(_images));
        await Task.Delay(300);

        Directory.Delete(_images, true);
        await Task.Delay(600);
        Directory.CreateDirectory(_images);

        var path = Path.Combine(_images, "after-restore.png");
        // Give the health loop time to notice and recreate the watch, then write.
        await Task.Delay(1500);
        await File.WriteAllBytesAsync(path, Bytes());

        await Eventually(() => _imageOptimiser.Processed.Contains(path, StringComparer.OrdinalIgnoreCase), 10_000, $"stats {service.Stats}");
        Assert.True(service.Stats.WatchRestarts >= 1 || service.Stats.Scans >= 1);
    }

    [Fact]
    public async Task FolderThatDoesNotExistYetIsPickedUpWhenItAppears()
    {
        var late = Path.Combine(_root, "late");
        Start(new FakeFolderSettings(late));
        await Task.Delay(400);

        Directory.CreateDirectory(late);
        await Task.Delay(1500);
        var path = Path.Combine(late, "hello.png");
        await File.WriteAllBytesAsync(path, Bytes());

        await Eventually(() => _imageOptimiser.Processed.Contains(path, StringComparer.OrdinalIgnoreCase), 10_000);
    }

    [Fact]
    public async Task ReconciliationScanQueuesOnlyRecentMediaFiles()
    {
        // Not started: no live watch, so the only way files reach the queue is the scan itself.
        var service = new DirectoryOptimisationService(_coordinator, new OptimisedFileRegistry(), NullLogger<DirectoryOptimisationService>.Instance, new FakeFolderSettings(_images), Fast, TimeProvider.System);
        var state = new DirectoryOptimisationService.RootState(new WatchRootSpec(_images, WatchTypes.Image));

        await File.WriteAllBytesAsync(Path.Combine(_images, "lost-event.png"), Bytes());
        var sub = Directory.CreateDirectory(Path.Combine(_images, "nested")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(sub, "lost-nested.jpg"), Bytes());
        await File.WriteAllBytesAsync(Path.Combine(_images, "notes.txt"), Bytes());
        await File.WriteAllBytesAsync(Path.Combine(_images, "clip.mp4"), Bytes());
        var old = Path.Combine(_images, "ancient.png");
        await File.WriteAllBytesAsync(old, Bytes());
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        var found = await service.ScanAsync(state, DateTime.UtcNow.AddMinutes(-1), CancellationToken.None);

        Assert.Equal(2, found);
        Assert.Equal(2, service.Stats.Tracked);
    }

    [Fact]
    public async Task ServiceCanBeRestartedAfterItStops()
    {
        var settings = new FakeFolderSettings(_images);
        var service = new DirectoryOptimisationService(_coordinator, new OptimisedFileRegistry(), NullLogger<DirectoryOptimisationService>.Instance, settings, Fast, TimeProvider.System);

        for (var round = 0; round < 3; round++)
        {
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => service.RunAsync(cts.Token));
            await Task.Delay(250);

            var path = Path.Combine(_images, $"round-{round}.png");
            await File.WriteAllBytesAsync(path, Bytes());
            await Eventually(() => _imageOptimiser.Processed.Contains(path, StringComparer.OrdinalIgnoreCase));

            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task VideoAndImageFoldersAreRoutedByType()
    {
        Start(new FakeFolderSettings(_images, _videos));
        await Task.Delay(300);

        var video = Path.Combine(_videos, "clip.mp4");
        var image = Path.Combine(_images, "pic.png");
        await File.WriteAllBytesAsync(video, Bytes());
        await File.WriteAllBytesAsync(image, Bytes());
        // A video dropped in the image folder must not be handled.
        await File.WriteAllBytesAsync(Path.Combine(_images, "wrong.mp4"), Bytes());

        await Eventually(() => _videoOptimiser.Processed.Contains(video, StringComparer.OrdinalIgnoreCase));
        await Eventually(() => _imageOptimiser.Processed.Contains(image, StringComparer.OrdinalIgnoreCase));
        await Task.Delay(300);
        Assert.Single(_videoOptimiser.Processed);
    }
}
