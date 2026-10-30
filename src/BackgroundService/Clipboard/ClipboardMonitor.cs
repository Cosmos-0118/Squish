using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using WinFormsClipboard = System.Windows.Forms.Clipboard;

namespace Squish.BackgroundService.Clipboard;

/// <summary>
/// Event-driven clipboard watcher. A message-only window on a dedicated STA thread subscribes with
/// <c>AddClipboardFormatListener</c> and Windows tells us when the clipboard changes, so an idle clipboard costs
/// nothing and a change is seen within a debounce interval rather than a poll interval.
/// </summary>
/// <remarks>
/// Reliability measures: bursts of updates are debounced into one capture; transient "clipboard is locked by
/// another process" failures are retried on a timer (not by sleeping the pump); a slow watchdog compares the
/// clipboard sequence number to catch notifications Windows failed to deliver and re-registers the listener (or
/// falls back to polling if registration is refused); a crashed pump is restarted; our own writes are recognised
/// by sequence number and never echoed back as user changes; and every queued STA work item is completed or
/// cancelled, never abandoned.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ClipboardMonitor : IDisposable
{
    private const uint WmTimer = 0x0113;
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmAppWork = 0x8001;
    private const uint WmAppStop = 0x8002;
    private const int HwndMessage = -3;
    private const int CaptureTimerId = 1;
    private const int WatchdogTimerId = 2;
    private const int MaxTextChars = 64 * 1024;

    private static readonly int[] RetryBackoffMs = { 20, 40, 80, 150, 250, 250, 250 };
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly ILogger<ClipboardMonitor> _logger;
    private readonly ClipboardMonitorOptions _options;
    private readonly object _gate = new();

    private Pump? _pump;
    private volatile ClipboardCaptureOptions _captureOptions = ClipboardCaptureOptions.All;
    private long _notifications;
    private long _captures;
    private long _skippedDuplicates;
    private long _captureRetries;
    private long _captureFailures;
    private long _missedNotifications;
    private long _pumpRestarts;
    private volatile bool _usingListener;
    private long _lastOwnWriteSequence;

    public ClipboardMonitor(ILogger<ClipboardMonitor> logger)
        : this(logger, ClipboardMonitorOptions.Default)
    {
    }

    internal ClipboardMonitor(ILogger<ClipboardMonitor> logger, ClipboardMonitorOptions options)
    {
        _logger = logger;
        _options = options;
    }

    /// <summary>Raised on the monitor thread for each distinct clipboard change. Handlers must return quickly.</summary>
    public event EventHandler<ClipboardSnapshot>? ClipboardChanged;

    public ClipboardCaptureOptions CaptureOptions
    {
        get => _captureOptions;
        set => _captureOptions = value ?? ClipboardCaptureOptions.All;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _pump is not null;
            }
        }
    }

    /// <summary>The clipboard sequence number right now (changes whenever any application changes the clipboard).</summary>
    public uint CurrentSequence => GetSequenceSafe();

    /// <summary>The sequence number produced by the most recent <see cref="WriteAsync"/>.</summary>
    public uint LastOwnWriteSequence => (uint)Interlocked.Read(ref _lastOwnWriteSequence);

    public ClipboardMonitorStats Stats => new(
        Interlocked.Read(ref _notifications),
        Interlocked.Read(ref _captures),
        Interlocked.Read(ref _skippedDuplicates),
        Interlocked.Read(ref _captureRetries),
        Interlocked.Read(ref _captureFailures),
        Interlocked.Read(ref _missedNotifications),
        Interlocked.Read(ref _pumpRestarts),
        _usingListener);

    public void Start()
    {
        Pump pump;
        lock (_gate)
        {
            if (_pump is not null)
            {
                return;
            }

            pump = new Pump();
            _pump = pump;
            pump.Thread = new Thread(() => ThreadMain(pump))
            {
                IsBackground = true,
                Name = "SquishClipboardMonitor"
            };
            pump.Thread.SetApartmentState(ApartmentState.STA);
            pump.Thread.Start();
        }

        if (!pump.Ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Clipboard monitor thread did not report ready within 5 seconds.");
        }
    }

    public void Stop()
    {
        Pump? pump;
        lock (_gate)
        {
            pump = _pump;
            _pump = null;
        }

        if (pump is null)
        {
            return;
        }

        pump.Cts.Cancel();
        var hwnd = pump.Hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WmAppStop, IntPtr.Zero, IntPtr.Zero);
        }

        if (pump.Thread is { } thread && thread != Thread.CurrentThread && !thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Clipboard monitor thread did not stop within 5 seconds; abandoning it.");
        }

        pump.CancelAll();
    }

    public void Dispose() => Stop();

    /// <summary>Runs <paramref name="action"/> on the monitor's STA thread (where clipboard access is safe).</summary>
    public Task RunOnStaAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Enqueue(_ =>
        {
            action();
            return 0;
        }, cancellationToken);
    }

    public Task<T> RunOnStaAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        return Enqueue(_ => func(), cancellationToken);
    }

    /// <summary>
    /// Replaces the clipboard contents. Returns the sequence number the write produced; the monitor treats that
    /// sequence as already handled so the write is never reported back as a user change.
    /// </summary>
    /// <param name="build">Builds the data object; called once, on the STA thread.</param>
    /// <param name="afterWrite">Runs on the STA thread once the data is on the clipboard (or the write failed); use it to dispose resources.</param>
    public Task<uint> WriteAsync(Func<DataObject> build, Action? afterWrite = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        return Enqueue(pump =>
        {
            try
            {
                var data = build();
                // Render first, then publish in one Open/Empty/Set/Close. SetDataObject(copy: true) calls
                // OleSetClipboard and then OleFlushClipboard, and each one is a separate clipboard generation.
                // Win+V records both, so a single optimised image showed up twice.
                var payloads = ClipboardNativeWriter.BuildPayloads(data);
                ClipboardNativeWriter.Place(payloads, pump.Hwnd);
                var sequence = GetSequenceSafe();
                pump.LastCaptured = sequence;
                pump.LastSeen = sequence;
                Interlocked.Exchange(ref _lastOwnWriteSequence, sequence);
                return sequence;
            }
            finally
            {
                afterWrite?.Invoke();
            }
        }, cancellationToken);
    }

    private Task<T> Enqueue<T>(Func<Pump, T> func, CancellationToken cancellationToken)
    {
        Pump? pump;
        lock (_gate)
        {
            pump = _pump;
        }

        if (pump is null)
        {
            throw new InvalidOperationException("Clipboard monitor is not running.");
        }

        var work = new Work<T>(pump, func, cancellationToken);
        pump.Queue.Enqueue(work);

        var hwnd = pump.Hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WmAppWork, IntPtr.Zero, IntPtr.Zero);
        }

        // If the pump went away between the check and the enqueue, nothing will ever run this item.
        if (pump.Cts.IsCancellationRequested)
        {
            pump.CancelAll();
        }

        return work.Task;
    }

    // ------------------------------------------------------------------ pump thread

    private void ThreadMain(Pump pump)
    {
        var failures = 0;

        while (!pump.Cts.IsCancellationRequested)
        {
            var started = Environment.TickCount64;

            try
            {
                RunPump(pump);
                if (pump.Cts.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogWarning("Clipboard message loop ended unexpectedly; restarting.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Clipboard message loop crashed; restarting.");
            }

            Interlocked.Increment(ref _pumpRestarts);
            failures = Environment.TickCount64 - started > 60_000 ? 1 : failures + 1;
            pump.Ready.Set();

            var delayMs = Math.Min(30_000, 500 << Math.Min(failures, 6));
            if (pump.Cts.Token.WaitHandle.WaitOne(delayMs))
            {
                break;
            }
        }

        pump.Ready.Set();
        pump.CancelAll();
    }

    private void RunPump(Pump pump)
    {
        using var window = new PumpWindow(this, pump);
        pump.Hwnd = window.Handle;

        try
        {
            var listening = AddClipboardFormatListener(pump.Hwnd);
            _usingListener = listening;
            if (!listening)
            {
                _logger.LogWarning("AddClipboardFormatListener failed ({Error}); falling back to polling the clipboard sequence number.", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            pump.Listening = listening;
            if (!pump.Initialised)
            {
                // Content already on the clipboard when we start is not a change.
                pump.LastCaptured = GetSequenceSafe();
                pump.LastSeen = pump.LastCaptured;
                pump.Initialised = true;
            }
            else if (GetSequenceSafe() != pump.LastCaptured)
            {
                ScheduleCapture(pump, _options.Debounce);
            }

            var watchdogMs = listening ? _options.WatchdogInterval : _options.FallbackPollInterval;
            SetTimer(pump.Hwnd, (UIntPtr)WatchdogTimerId, (uint)watchdogMs.TotalMilliseconds, IntPtr.Zero);

            pump.Ready.Set();
            if (!pump.Queue.IsEmpty)
            {
                PostMessage(pump.Hwnd, WmAppWork, IntPtr.Zero, IntPtr.Zero);
            }

            while (true)
            {
                var result = GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            var hwnd = pump.Hwnd;
            if (hwnd != IntPtr.Zero)
            {
                KillTimer(hwnd, (UIntPtr)CaptureTimerId);
                KillTimer(hwnd, (UIntPtr)WatchdogTimerId);
                if (pump.Listening)
                {
                    RemoveClipboardFormatListener(hwnd);
                }
            }

            pump.Listening = false;
            pump.Hwnd = IntPtr.Zero;
        }
    }

    private bool HandleMessage(Pump pump, ref Message m)
    {
        switch ((uint)m.Msg)
        {
            case WmClipboardUpdate:
                Interlocked.Increment(ref _notifications);
                pump.LastSeen = GetSequenceSafe();
                pump.Attempt = 0;
                ScheduleCapture(pump, _options.Debounce);
                return true;

            case WmTimer:
                var id = (int)m.WParam;
                if (id == CaptureTimerId)
                {
                    KillTimer(pump.Hwnd, (UIntPtr)CaptureTimerId);
                    AttemptCapture(pump);
                    return true;
                }

                if (id == WatchdogTimerId)
                {
                    Watchdog(pump);
                    return true;
                }

                return false;

            case WmAppWork:
                DrainWork(pump);
                return true;

            case WmAppStop:
                PostQuitMessage(0);
                return true;

            default:
                return false;
        }
    }

    private void DrainWork(Pump pump)
    {
        while (pump.Queue.TryDequeue(out var work))
        {
            work.Run();
        }
    }

    private void Watchdog(Pump pump)
    {
        var sequence = GetSequenceSafe();
        if (sequence == 0 || sequence == pump.LastSeen)
        {
            return;
        }

        pump.LastSeen = sequence;

        if (pump.Listening)
        {
            // The clipboard changed and Windows never told us (listener silently dropped, e.g. after a session switch).
            Interlocked.Increment(ref _missedNotifications);
            _logger.LogWarning("Clipboard changed without a notification; re-registering the listener.");
            RemoveClipboardFormatListener(pump.Hwnd);
            if (!AddClipboardFormatListener(pump.Hwnd))
            {
                pump.Listening = false;
                _usingListener = false;
                SetTimer(pump.Hwnd, (UIntPtr)WatchdogTimerId, (uint)_options.FallbackPollInterval.TotalMilliseconds, IntPtr.Zero);
                _logger.LogWarning("Could not re-register the clipboard listener; polling instead.");
            }
        }

        pump.Attempt = 0;
        ScheduleCapture(pump, _options.Debounce);
    }

    private void ScheduleCapture(Pump pump, TimeSpan delay)
    {
        var now = Environment.TickCount64;
        var ms = (long)delay.TotalMilliseconds;

        if (pump.FirstPendingTick == 0)
        {
            pump.FirstPendingTick = now;
        }
        else if (now - pump.FirstPendingTick >= _options.MaxDebounce.TotalMilliseconds)
        {
            // Continuous churn must not postpone the capture forever.
            ms = 1;
        }

        SetTimer(pump.Hwnd, (UIntPtr)CaptureTimerId, (uint)Math.Max(1, ms), IntPtr.Zero);
    }

    private void AttemptCapture(Pump pump)
    {
        var sequence = GetSequenceSafe();
        if (sequence == 0)
        {
            pump.FirstPendingTick = 0;
            return;
        }

        if (sequence == pump.LastCaptured)
        {
            // Our own write, or a notification for content we already handled.
            Interlocked.Increment(ref _skippedDuplicates);
            pump.FirstPendingTick = 0;
            return;
        }

        var snapshot = TryCapture(sequence, out var transient);

        if (snapshot is null && transient && pump.Attempt < RetryBackoffMs.Length)
        {
            // Another process has the clipboard open. Try again shortly without blocking the message loop.
            Interlocked.Increment(ref _captureRetries);
            ScheduleCapture(pump, TimeSpan.FromMilliseconds(RetryBackoffMs[pump.Attempt++]));
            return;
        }

        if (snapshot is not null && GetSequenceSafe() != sequence && pump.Attempt < RetryBackoffMs.Length)
        {
            // The clipboard changed while we were reading it; what we read may be a mix of two copies. Read again.
            pump.Attempt++;
            ScheduleCapture(pump, _options.Debounce);
            return;
        }

        pump.FirstPendingTick = 0;
        pump.Attempt = 0;
        pump.LastCaptured = sequence;

        if (snapshot is null)
        {
            Interlocked.Increment(ref _captureFailures);
            _logger.LogDebug("Clipboard snapshot capture failed.");
            return;
        }

        Interlocked.Increment(ref _captures);

        try
        {
            ClipboardChanged?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClipboardChanged handler threw an exception.");
        }
    }

    // ------------------------------------------------------------------ reading

    private ClipboardSnapshot? TryCapture(uint sequence, out bool transient)
    {
        transient = false;
        try
        {
            return CaptureCore(sequence, _captureOptions);
        }
        catch (ExternalException ex)
        {
            _logger.LogDebug(ex, "Clipboard unavailable for snapshot (transient).");
            transient = true;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture clipboard snapshot.");
            return null;
        }
    }

    private ClipboardSnapshot CaptureCore(uint sequence, ClipboardCaptureOptions options)
    {
        var dataObject = WinFormsClipboard.GetDataObject();
        if (dataObject is null)
        {
            return ClipboardSnapshot.Empty with { Sequence = sequence };
        }

        // Anything we wrote ourselves carries the marker: skip all the expensive reads.
        if (dataObject.GetDataPresent(ClipboardFormats.OptimisationStatus, false))
        {
            return new ClipboardSnapshot(Array.Empty<string>(), null, null, true, sequence);
        }

        var filePaths = Array.Empty<string>();
        if (options.Files && dataObject.GetDataPresent(DataFormats.FileDrop, true) &&
            dataObject.GetData(DataFormats.FileDrop, true) is string[] dropList && dropList.Length > 0)
        {
            filePaths = dropList.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        }

        var imageBytes = options.Image ? TryReadImage(dataObject) : null;

        string? text = null;
        if (options.Text && dataObject.GetDataPresent(DataFormats.Text, true))
        {
            text = dataObject.GetData(DataFormats.Text, true) as string;
            if (text is { Length: > MaxTextChars })
            {
                text = text[..MaxTextChars];
            }
        }

        return new ClipboardSnapshot(filePaths, imageBytes, text, false, sequence);
    }

    private byte[]? TryReadImage(System.Windows.Forms.IDataObject dataObject)
    {
        // Prefer the PNG that browsers, Snipping Tool and image editors publish: it keeps transparency and
        // costs a memory copy instead of a decode + re-encode of a possibly multi-megapixel bitmap.
        try
        {
            if (dataObject.GetDataPresent("PNG", false) && dataObject.GetData("PNG", false) is MemoryStream stream)
            {
                var bytes = stream.ToArray();
                if (bytes.Length > PngSignature.Length && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
                {
                    return bytes;
                }
            }
        }
        catch (Exception ex) when (ex is not ExternalException)
        {
            _logger.LogDebug(ex, "Clipboard PNG format unreadable; falling back to the bitmap.");
        }

        try
        {
            if (!WinFormsClipboard.ContainsImage())
            {
                return null;
            }

            using var image = WinFormsClipboard.GetImage();
            if (image is null)
            {
                return null;
            }

            using var ms = new MemoryStream();
            image.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (ExternalException ex)
        {
            _logger.LogDebug(ex, "Clipboard image unavailable during capture.");
            throw;
        }
    }

    private uint GetSequenceSafe()
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

    // ------------------------------------------------------------------ plumbing

    private sealed class Pump
    {
        public Thread? Thread { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        public ConcurrentQueue<IWork> Queue { get; } = new();

        public ManualResetEventSlim Ready { get; } = new(false);

        public volatile IntPtr Hwnd;

        // Touched only on the pump thread.
        public bool Initialised;
        public bool Listening;
        public uint LastCaptured;
        public uint LastSeen;
        public long FirstPendingTick;
        public int Attempt;

        public void CancelAll()
        {
            while (Queue.TryDequeue(out var work))
            {
                work.Cancel();
            }
        }
    }

    private interface IWork
    {
        void Run();

        void Cancel();
    }

    private sealed class Work<T> : IWork
    {
        private readonly Pump _pump;
        private readonly Func<Pump, T> _func;
        private readonly CancellationToken _token;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Work(Pump pump, Func<Pump, T> func, CancellationToken token)
        {
            _pump = pump;
            _func = func;
            _token = token;
        }

        public Task<T> Task => _tcs.Task;

        public void Run()
        {
            if (_token.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(_token);
                return;
            }

            try
            {
                _tcs.TrySetResult(_func(_pump));
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public void Cancel() => _tcs.TrySetCanceled();
    }

    private sealed class PumpWindow : NativeWindow, IDisposable
    {
        private readonly ClipboardMonitor _owner;
        private readonly Pump _pump;

        public PumpWindow(ClipboardMonitor owner, Pump pump)
        {
            _owner = owner;
            _pump = pump;
            CreateHandle(new CreateParams { Parent = new IntPtr(HwndMessage), Caption = "SquishClipboardMonitor" });
        }

        protected override void WndProc(ref Message m)
        {
            // An exception escaping a NativeWindow WndProc would surface WinForms' thread-exception dialog.
            try
            {
                if (_owner.HandleMessage(_pump, ref m))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _owner._logger.LogError(ex, "Clipboard monitor message handler failed.");
                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetClipboardSequenceNumber();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapseMs, IntPtr proc);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
}

internal sealed record ClipboardMonitorOptions
{
    /// <summary>Quiet time after a notification before reading; apps often publish several formats in quick succession.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>Upper bound on how long continuous notifications may postpone a capture.</summary>
    public TimeSpan MaxDebounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the watchdog compares sequence numbers to catch undelivered notifications.</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Polling cadence used only when the listener cannot be registered.</summary>
    public TimeSpan FallbackPollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public static ClipboardMonitorOptions Default { get; } = new();
}
