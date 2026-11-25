using System.Collections.Concurrent;
using System.IO;
using Squish.BackgroundService.Automation;
using Xunit;

namespace BackgroundService.Tests;

public sealed class NativeWatcherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("native-watch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met in time.");
    }

    private FolderWatcher Create(bool native, ConcurrentQueue<(string Path, WatchTypes Type, bool Renamed)> hits, bool recursive = true, WatchTypes types = WatchTypes.Image | WatchTypes.Video) =>
        new(_root, types, (path, type, renamed) => hits.Enqueue((path, type, renamed)), (_, _) => { }, recursive, useNative: native);

    [Fact]
    public void NativeBackendIsUsedOnThisMachine()
    {
        using var watcher = Create(native: true, new());
        Assert.Equal("native", watcher.Backend);
    }

    [Fact]
    public void FileSystemWatcherBackendCanStillBeForced()
    {
        using var watcher = Create(native: false, new());
        Assert.Equal("FileSystemWatcher", watcher.Backend);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothBackendsReportCreatedMediaFilesAndNothingElse(bool native)
    {
        var hits = new ConcurrentQueue<(string Path, WatchTypes Type, bool Renamed)>();
        using var watcher = Create(native, hits);

        Directory.CreateDirectory(Path.Combine(_root, "photos.png")); // a folder that looks like media
        var photo = Path.Combine(_root, "shot.PNG");
        var clip = Path.Combine(_root, "clip.mp4");
        await File.WriteAllBytesAsync(photo, new byte[100]);
        await File.WriteAllBytesAsync(clip, new byte[100]);
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "report.docx"), "x"); // document: not in the allowed types

        await Eventually(() => hits.Any(h => h.Path == photo) && hits.Any(h => h.Path == clip));
        await Task.Delay(200);

        Assert.All(hits, h => Assert.Contains(Path.GetExtension(h.Path).ToLowerInvariant(), new[] { ".png", ".mp4" }));
        Assert.DoesNotContain(hits, h => h.Path.EndsWith("photos.png", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hits, h => h.Path == photo && h.Type == WatchTypes.Image);
        Assert.Contains(hits, h => h.Path == clip && h.Type == WatchTypes.Video);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenameIntoPlaceIsReportedAsRenamedWithTheNewName(bool native)
    {
        var hits = new ConcurrentQueue<(string Path, WatchTypes Type, bool Renamed)>();
        var partial = Path.Combine(_root, "download.part");
        await File.WriteAllBytesAsync(partial, new byte[50]);
        using var watcher = Create(native, hits);

        var final = Path.Combine(_root, "download.jpg");
        File.Move(partial, final);

        await Eventually(() => hits.Any(h => h.Path == final && h.Renamed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlatWatchIgnoresSubfolders(bool native)
    {
        var hits = new ConcurrentQueue<(string Path, WatchTypes Type, bool Renamed)>();
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        using var watcher = Create(native, hits, recursive: false);

        var nested = Path.Combine(sub, "deep.png");
        var top = Path.Combine(_root, "top.png");
        await File.WriteAllBytesAsync(nested, new byte[10]);
        await File.WriteAllBytesAsync(top, new byte[10]);

        try
        {
            await Eventually(() => hits.Any(h => h.Path == top));
        }
        catch (Exception)
        {
            throw new Exception("hits: " + string.Join(" | ", hits.Select(h => h.Path)) + " backend=" + watcher.Backend + " faulted=" + watcher.IsFaulted + " records=" + watcher.NativeRecordsSeen);
        }

        await Task.Delay(200);
        Assert.DoesNotContain(hits, h => h.Path == nested);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecursiveWatchSeesSubfolders(bool native)
    {
        var hits = new ConcurrentQueue<(string Path, WatchTypes Type, bool Renamed)>();
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub", "inner")).FullName;
        using var watcher = Create(native, hits);

        var nested = Path.Combine(sub, "deep.png");
        await File.WriteAllBytesAsync(nested, new byte[10]);

        await Eventually(() => hits.Any(h => h.Path == nested));
    }

    [Fact]
    public async Task AllowedTypesCanBeChangedWhileWatching()
    {
        var hits = new ConcurrentQueue<(string Path, WatchTypes Type, bool Renamed)>();
        using var watcher = Create(native: true, hits, types: WatchTypes.Image);

        await File.WriteAllBytesAsync(Path.Combine(_root, "a.mp4"), new byte[10]);
        await Task.Delay(300);
        Assert.Empty(hits);

        watcher.AllowedTypes = WatchTypes.Image | WatchTypes.Video;
        var b = Path.Combine(_root, "b.mp4");
        await File.WriteAllBytesAsync(b, new byte[10]);
        await Eventually(() => hits.Any(h => h.Path == b));
    }

    [Fact]
    public async Task DisposeStopsDeliveryAndReleasesTheFolder()
    {
        var hits = new ConcurrentQueue<(string Path, WatchTypes Type, bool Renamed)>();
        var watcher = Create(native: true, hits);
        watcher.Dispose();

        await File.WriteAllBytesAsync(Path.Combine(_root, "late.png"), new byte[10]);
        await Task.Delay(300);
        Assert.Empty(hits);

        // The directory handle is closed: the folder can be deleted while the watcher object is gone.
        Directory.Delete(_root, recursive: true);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task WatchedFolderBeingDeletedFaultsTheWatcher()
    {
        var faulted = new TaskCompletionSource();
        using var watcher = new FolderWatcher(_root, WatchTypes.Image, (_, _, _) => { }, (_, _) => faulted.TrySetResult(), true, useNative: true);

        Directory.Delete(_root, recursive: true);

        // Deleting a watched folder is reported as an access error by the OS, as it is with FileSystemWatcher.
        var finished = await Task.WhenAny(faulted.Task, Task.Delay(3000));
        Assert.True(ReferenceEquals(finished, faulted.Task) || watcher.IsFaulted, "The watcher did not notice its folder disappearing.");
    }
}
