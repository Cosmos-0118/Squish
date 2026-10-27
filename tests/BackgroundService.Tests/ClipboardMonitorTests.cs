using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Squish.BackgroundService.Clipboard;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BackgroundService.Tests;

/// <summary>
/// These tests drive the real Windows clipboard, so they overwrite whatever is on it. They only run when
/// SQUISH_CLIPBOARD_TESTS=1 is set.
/// </summary>
public sealed class ClipboardFactAttribute : FactAttribute
{
    public ClipboardFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SQUISH_CLIPBOARD_TESTS") != "1")
        {
            Skip = "Touches the real clipboard; set SQUISH_CLIPBOARD_TESTS=1 to run.";
        }
    }
}

public sealed class ClipboardMonitorTests : IDisposable
{
    private readonly ClipboardMonitor _monitor = new(NullLogger<ClipboardMonitor>.Instance);
    private readonly ConcurrentQueue<(ClipboardSnapshot Snapshot, long Tick)> _seen = new();

    public ClipboardMonitorTests()
    {
        _monitor.ClipboardChanged += (_, snapshot) => _seen.Enqueue((snapshot, Stopwatch.GetTimestamp()));
    }

    public void Dispose() => _monitor.Dispose();

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("STA action failed", error);
        }
    }

    private static void SetText(string text) => RunSta(() => Clipboard.SetDataObject(text, true, 20, 25));

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not met in time.");
    }

    [ClipboardFact]
    public async Task ReportsATextCopyQuickly()
    {
        _monitor.Start();
        var marker = "hello-" + Guid.NewGuid().ToString("N");
        var before = Stopwatch.GetTimestamp();

        SetText(marker);
        await Eventually(() => _seen.Any(s => s.Snapshot.Text == marker));

        var latency = Stopwatch.GetElapsedTime(before, _seen.First(s => s.Snapshot.Text == marker).Tick);
        Assert.True(latency < TimeSpan.FromMilliseconds(400), $"latency was {latency.TotalMilliseconds:0} ms");
        Assert.True(_monitor.Stats.UsingListener);
    }

    [ClipboardFact]
    public async Task ContentPresentAtStartIsNotReported()
    {
        SetText("already-here");
        _monitor.Start();
        await Task.Delay(500);

        Assert.Empty(_seen);
    }

    [ClipboardFact]
    public async Task OurOwnWritesAreNeverReportedBackButTheNextUserCopyIs()
    {
        _monitor.Start();

        var sequence = await _monitor.WriteAsync(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.Text, "written-by-us");
            return data;
        });
        await Task.Delay(600);
        Assert.Empty(_seen);
        Assert.Equal(sequence, _monitor.LastOwnWriteSequence);

        SetText("user-copy-after");
        await Eventually(() => _seen.Any(s => s.Snapshot.Text == "user-copy-after"));
        Assert.Single(_seen);
    }

    [ClipboardFact]
    public async Task ABurstOfCopiesCollapsesAndEndsOnTheLastValue()
    {
        _monitor.Start();

        for (var i = 0; i < 30; i++)
        {
            SetText("burst-" + i);
        }

        await Eventually(() => _seen.Any(s => s.Snapshot.Text == "burst-29"));
        await Task.Delay(300);

        Assert.Equal("burst-29", _seen.Last().Snapshot.Text);
        Assert.True(_seen.Count < 30, $"{_seen.Count} snapshots for 30 copies");
    }

    [ClipboardFact]
    public async Task ReportsAnImageAsPng()
    {
        _monitor.Start();

        RunSta(() =>
        {
            using var bitmap = new Bitmap(64, 48);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.CornflowerBlue);
            }

            Clipboard.SetImage(bitmap);
        });

        await Eventually(() => _seen.Any(s => s.Snapshot.ImageBytes is { Length: > 8 }));
        var bytes = _seen.First(s => s.Snapshot.ImageBytes is not null).Snapshot.ImageBytes!;
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }

    [ClipboardFact]
    public async Task SkipsReadingImageAndTextWhenNotWanted()
    {
        _monitor.CaptureOptions = new ClipboardCaptureOptions(Files: false, Image: false, Text: false);
        _monitor.Start();

        RunSta(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.Text, "ignored text");
            using var bitmap = new Bitmap(8, 8);
            data.SetData(DataFormats.Bitmap, bitmap);
            Clipboard.SetDataObject(data, true);
        });

        await Eventually(() => !_seen.IsEmpty);
        var snapshot = _seen.Last().Snapshot;
        Assert.Null(snapshot.ImageBytes);
        Assert.Null(snapshot.Text);
        Assert.Empty(snapshot.FilePaths);
    }

    [ClipboardFact]
    public async Task OptimisationMarkerIsDetectedWithoutReadingContent()
    {
        _monitor.Start();

        RunSta(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.Text, "marked");
            data.SetData(ClipboardFormats.OptimisationStatus, "true");
            Clipboard.SetDataObject(data, true);
        });

        await Eventually(() => !_seen.IsEmpty);
        var snapshot = _seen.Last().Snapshot;
        Assert.True(snapshot.HasOptimisationMarker);
        Assert.Null(snapshot.Text);
    }

    [ClipboardFact]
    public async Task RetriesWhileAnotherProcessHoldsTheClipboardOpen()
    {
        _monitor.Start();
        SetText("locked-" + Guid.NewGuid().ToString("N"));

        // Hold the clipboard open for a while, as a clipboard manager or remote-desktop client would.
        RunSta(() =>
        {
            var deadline = Stopwatch.StartNew();
            while (!OpenClipboard(IntPtr.Zero) && deadline.ElapsedMilliseconds < 1000)
            {
                Thread.Sleep(1);
            }

            Thread.Sleep(350);
            CloseClipboard();
        });

        await Eventually(() => !_seen.IsEmpty, 8000);
    }

    [ClipboardFact]
    public async Task StopCompletesOrCancelsQueuedWorkAndRestartWorks()
    {
        for (var round = 0; round < 15; round++)
        {
            _monitor.Start();
            var marker = "round-" + round;
            SetText(marker);
            await Eventually(() => _seen.Any(s => s.Snapshot.Text == marker));

            var queued = _monitor.RunOnStaAsync(() => Thread.CurrentThread.GetApartmentState());
            Assert.Equal(ApartmentState.STA, await queued.WaitAsync(TimeSpan.FromSeconds(3)));

            _monitor.Stop();
            Assert.False(_monitor.IsRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => _monitor.RunOnStaAsync(() => 1));
        }
    }

    [ClipboardFact]
    public async Task WorkQueuedFromManyThreadsAllRunsOnTheStaThread()
    {
        _monitor.Start();

        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(() => _monitor.RunOnStaAsync(() => (i, Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState())))).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, r => Assert.Equal(ApartmentState.STA, r.Item3));
        Assert.Single(results.Select(r => r.Item2).Distinct());
        Assert.Equal(200, results.Select(r => r.i).Distinct().Count());
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
}
