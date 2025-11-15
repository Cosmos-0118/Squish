using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinFormsClipboard = System.Windows.Forms.Clipboard;
using DataFormats = System.Windows.Forms.DataFormats;

namespace ClopWindows.BackgroundService.Clipboard;

/// <summary>
/// Polls the Windows clipboard from an STA thread and raises lightweight snapshots when changes occur.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClipboardMonitor : IDisposable
{
    private readonly ILogger<ClipboardMonitor> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private BlockingCollection<Func<CancellationToken, Task>>? _workQueue;

    public ClipboardMonitor(ILogger<ClipboardMonitor> logger)
        : this(logger, TimeSpan.FromMilliseconds(500))
    {
    }

    public ClipboardMonitor(ILogger<ClipboardMonitor> logger, TimeSpan pollInterval)
    {
        _logger = logger;
        _pollInterval = pollInterval;
    }

    public event EventHandler<ClipboardSnapshot>? ClipboardChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _workQueue = new BlockingCollection<Func<CancellationToken, Task>>();
            _thread = new Thread(() => Run(_cts, _workQueue))
            {
                IsBackground = true,
                Name = "ClopClipboardMonitor"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_thread is null)
            {
                return;
            }

            try
            {
                _cts?.Cancel();
                _workQueue?.CompleteAdding();
                _thread.Join();
            }
            catch (ThreadStateException)
            {
                // ignore if the thread is already stopping
            }
            finally
            {
                _workQueue?.Dispose();
                _cts?.Dispose();
                _thread = null;
                _cts = null;
                _workQueue = null;
            }
        }
    }

    public Task RunOnStaAsync(Func<Task> action)
    {
        lock (_gate)
        {
            if (_workQueue is null || _cts is null)
            {
                throw new InvalidOperationException("Clipboard monitor is not running.");
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _workQueue.Add(async token =>
            {
                if (token.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(token);
                    return;
                }

                try
                {
                    await action().ConfigureAwait(true);
                    tcs.TrySetResult();
                }
                catch (OperationCanceledException oce)
                {
                    tcs.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return tcs.Task;
        }
    }

    public void Dispose() => Stop();

    private void Run(CancellationTokenSource? cts, BlockingCollection<Func<CancellationToken, Task>> workQueue)
    {
        var token = cts!.Token;
        var lastSequence = GetClipboardSequenceNumberSafe();

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (workQueue.TryTake(out var work, _pollInterval))
                {
                    try
                    {
                        work(token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        // best effort cancellation
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Clipboard STA work item failed.");
                    }
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                // collection completed, exit loop
                break;
            }

            var current = GetClipboardSequenceNumberSafe();
            if (current == 0 || current == lastSequence)
            {
                continue;
            }

            var snapshot = CaptureSnapshotWithRetries();
            if (snapshot is null)
            {
                lastSequence = current;
                continue;
            }

            lastSequence = current;

            try
            {
                ClipboardChanged?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ClipboardChanged handler threw an exception.");
            }
        }
    }

    private uint GetClipboardSequenceNumberSafe()
    {
        try
        {
            return GetClipboardSequenceNumber();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read clipboard sequence number.");
            return 0;
        }
    }

    private ClipboardSnapshot? CaptureSnapshotWithRetries()
    {
        const int maxAttempts = 6;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var success = TryCaptureSnapshot(out var snapshot, out var retryable);
            if (success)
            {
                return snapshot;
            }

            if (!retryable)
            {
                return null;
            }

            var backoff = Math.Min(100, 20 * attempt);
            Thread.Sleep(backoff);
        }

        _logger.LogDebug("Clipboard snapshot capture failed after {Attempts} attempts.", maxAttempts);
        return null;
    }

    private bool TryCaptureSnapshot(out ClipboardSnapshot snapshot, out bool retryable)
    {
        try
        {
            snapshot = CaptureSnapshotCore();
            retryable = false;
            return true;
        }
        catch (ExternalException ex)
        {
            _logger.LogDebug(ex, "Clipboard unavailable for snapshot (transient).");
            snapshot = ClipboardSnapshot.Empty;
            retryable = true;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture clipboard snapshot.");
            snapshot = ClipboardSnapshot.Empty;
            retryable = false;
            return false;
        }
    }

    private ClipboardSnapshot CaptureSnapshotCore()
    {
        var dataObject = WinFormsClipboard.GetDataObject();
        if (dataObject is null)
        {
            return ClipboardSnapshot.Empty;
        }

        var hasMarker = dataObject.GetDataPresent(ClipboardFormats.OptimisationStatus, false);

        var filePaths = Array.Empty<string>();
        if (dataObject.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop, true))
        {
            var data = dataObject.GetData(System.Windows.Forms.DataFormats.FileDrop, true);
            if (data is string[] dropList && dropList.Length > 0)
            {
                filePaths = dropList.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            }
        }

        byte[]? imageBytes = null;
        try
        {
            if (WinFormsClipboard.ContainsImage())
            {
                using var image = WinFormsClipboard.GetImage();
                if (image is not null)
                {
                    using var clone = new Bitmap(image);
                    using var ms = new MemoryStream();
                    clone.Save(ms, ImageFormat.Png);
                    imageBytes = ms.ToArray();
                }
            }
        }
        catch (ExternalException ex)
        {
            _logger.LogDebug(ex, "Clipboard image unavailable during capture.");
        }

        string? text = null;
        if (dataObject.GetDataPresent(System.Windows.Forms.DataFormats.Text, true))
        {
            text = dataObject.GetData(System.Windows.Forms.DataFormats.Text, true) as string;
        }

        return new ClipboardSnapshot(filePaths, imageBytes, text, hasMarker);
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetClipboardSequenceNumber();
}
