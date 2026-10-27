using System;
using System.IO;
using System.Threading;

namespace Squish.BackgroundService.Automation;

/// <summary>
/// One OS directory watch. The event callbacks do nothing but a string-only extension test and a hand-off, because
/// they run on the kernel's notification thread: any I/O or lock contention here is what overflows the buffer and
/// loses events.
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    // Non-paged memory per watch; 64 KB holds roughly 1,000 events, 8x the 8 KB default.
    internal const int BufferBytes = 64 * 1024;

    private readonly FileSystemWatcher _watcher;
    private readonly Action<string, WatchTypes, bool> _onHit;
    private readonly Action<FolderWatcher, Exception> _onFault;
    private int _faulted;
    private int _overflowed;
    private long _eventCount;
    private volatile int _allowedTypes;

    public FolderWatcher(string root, WatchTypes allowedTypes, Action<string, WatchTypes, bool> onHit, Action<FolderWatcher, Exception> onFault)
    {
        Root = root;
        _allowedTypes = (int)allowedTypes;
        _onHit = onHit;
        _onFault = onFault;

        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = BufferBytes,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    public string Root { get; }

    public WatchTypes AllowedTypes
    {
        get => (WatchTypes)_allowedTypes;
        set => _allowedTypes = (int)value;
    }

    /// <summary>True once the OS watch reported something other than a recoverable overflow; the watch must be recreated.</summary>
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>True when the kernel buffer overflowed since the last <see cref="ConsumeOverflow"/>: events were lost.</summary>
    public bool HasOverflowed => Volatile.Read(ref _overflowed) != 0;

    public long EventCount => Interlocked.Read(ref _eventCount);

    public bool ConsumeOverflow() => Interlocked.Exchange(ref _overflowed, 0) != 0;

    private void OnChanged(object sender, FileSystemEventArgs e) => Hit(e.FullPath, renamed: false);

    // Only the new name matters: "download.part" -> "photo.jpg" is the dominant pattern for finished downloads.
    private void OnRenamed(object sender, RenamedEventArgs e) => Hit(e.FullPath, renamed: true);

    private void Hit(string? path, bool renamed)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var type = MediaExtensionFilter.Classify(path);
        if (type == WatchTypes.None || (type & (WatchTypes)_allowedTypes) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _eventCount);
        try
        {
            _onHit(path, type, renamed);
        }
        catch
        {
            // Never let a consumer exception unwind into the watcher thread.
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        var exception = e.GetException();
        if (exception is InternalBufferOverflowException)
        {
            // The watch itself is still alive; some events are not. The owner reconciles with a directory scan.
            Volatile.Write(ref _overflowed, 1);
            try
            {
                _onFault(this, exception);
            }
            catch
            {
                // ignored
            }

            return;
        }

        Volatile.Write(ref _faulted, 1);
        try
        {
            _onFault(this, exception);
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnChanged;
        _watcher.Changed -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }
}
