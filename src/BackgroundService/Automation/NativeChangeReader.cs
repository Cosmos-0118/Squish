using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Squish.BackgroundService.Automation;

/// <summary>
/// Directory change notifications straight from <c>ReadDirectoryChangesExW</c>. Compared with <see cref="FileSystemWatcher"/>
/// the kernel packet already carries each file's attributes, so folders are dropped without a single call, and the file name
/// is tested against the media extensions as a span before any string is built: events for files Squish does not care
/// about (the vast majority in a busy folder) cost no allocation at all.
/// </summary>
/// <remarks>
/// The first read is issued (overlapped) before <see cref="TryStart"/> returns, so nothing that happens after the watcher
/// exists can be missed; a dedicated thread then waits for completions and re-issues. The kernel keeps buffering on the
/// directory handle between reads. <see cref="TryStart"/> returns null where the extended API is unavailable (before
/// Windows 10 1709, some network file systems) so the caller falls back to <see cref="FileSystemWatcher"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class NativeChangeReader : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint Overlapped = 0x40000000;
    private const uint NotifyFileName = 0x1;
    private const uint NotifySize = 0x8;
    private const uint NotifyLastWrite = 0x10;
    private const int ExtendedInformationClass = 2;
    private const int BufferBytes = 64 * 1024;
    private const int HeaderBytes = 84;
    private const uint DirectoryAttribute = 0x10;
    private const int ErrorIoPending = 997;
    private const int ErrorOperationAborted = 995;
    private const int ErrorNotifyEnumDir = 1022;

    private const uint ActionAdded = 1;
    private const uint ActionModified = 3;
    private const uint ActionRenamedNew = 5;

    private readonly SafeFileHandle _directory;
    private readonly string _root;
    private readonly bool _recursive;
    private readonly Func<WatchTypes> _allowedTypes;
    private readonly Action<string, WatchTypes, bool> _onHit;
    private readonly Action<Exception> _onError;
    private readonly Thread _thread;
    private readonly byte[] _buffer = new byte[BufferBytes];
    private readonly GCHandle _pin;
    private readonly IntPtr _overlapped;
    private readonly ManualResetEvent _completed = new(false);
    private readonly ManualResetEventSlim _firstRead = new(false);
    private volatile bool _stopping;
    private volatile bool _unsupported;
    private long _recordsSeen;

    private NativeChangeReader(
        SafeFileHandle directory,
        string root,
        bool recursive,
        Func<WatchTypes> allowedTypes,
        Action<string, WatchTypes, bool> onHit,
        Action<Exception> onError)
    {
        _directory = directory;
        _root = root;
        _recursive = recursive;
        _allowedTypes = allowedTypes;
        _onHit = onHit;
        _onError = onError;
        _pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        _thread = new Thread(Run) { IsBackground = true, Name = "squish-dirwatch" };
    }

    /// <summary>Number of change records the kernel delivered (before filtering).</summary>
    public long RecordsSeen => Interlocked.Read(ref _recordsSeen);

    /// <summary>Starts watching <paramref name="root"/>, or returns null if the extended notifications cannot be used there.</summary>
    public static NativeChangeReader? TryStart(
        string root,
        bool recursive,
        Func<WatchTypes> allowedTypes,
        Action<string, WatchTypes, bool> onHit,
        Action<Exception> onError)
    {
        if (Environment.GetEnvironmentVariable("SQUISH_NATIVE_WATCH") == "0")
        {
            return null;
        }

        SafeFileHandle handle;
        try
        {
            handle = CreateFileW(root, FileListDirectory, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | Overlapped, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var reader = new NativeChangeReader(handle, root, recursive, allowedTypes, onHit, onError);
        reader._thread.Start();

        // The read must be issued by the thread that waits for it (a pending request is cancelled when its issuing thread
        // exits), but it must also be queued before the caller can change anything: wait for that here.
        if (!reader._firstRead.Wait(TimeSpan.FromSeconds(5)) || reader._unsupported)
        {
            reader.Dispose();
            return null;
        }

        return reader;
    }

    /// <summary>Queues the next read. False means the call is not supported here; throws for anything else unexpected.</summary>
    private bool IssueRead(out int error)
    {
        error = 0;
        _completed.Reset();
        var overlapped = new NativeOverlapped { EventHandle = _completed.SafeWaitHandle.DangerousGetHandle() };
        Marshal.StructureToPtr(overlapped, _overlapped, fDeleteOld: false);

        var ok = ReadDirectoryChangesExW(
            _directory,
            _pin.AddrOfPinnedObject(),
            (uint)_buffer.Length,
            _recursive,
            NotifyFileName | NotifySize | NotifyLastWrite,
            out _,
            _overlapped,
            IntPtr.Zero,
            ExtendedInformationClass);

        if (ok)
        {
            return true;
        }

        error = Marshal.GetLastWin32Error();
        if (error == ErrorIoPending)
        {
            return true;
        }

        if (error is 1 or 50 or 120)
        {
            return false;
        }

        throw new Win32Exception(error);
    }

    private void Run()
    {
        try
        {
            try
            {
                if (!IssueRead(out _))
                {
                    // Unsupported API or file system: the caller uses FileSystemWatcher instead.
                    _unsupported = true;
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
                _unsupported = true;
                return;
            }
            finally
            {
                _firstRead.Set();
            }

            while (!_stopping)
            {
                if (!GetOverlappedResult(_directory, _overlapped, out var returned, bWait: true))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (_stopping || error == ErrorOperationAborted)
                    {
                        return;
                    }

                    if (error == ErrorNotifyEnumDir)
                    {
                        _onError(new InternalBufferOverflowException("The directory change buffer overflowed."));
                    }
                    else
                    {
                        _onError(new Win32Exception(error));
                        return;
                    }
                }
                else if (returned == 0)
                {
                    // Success with no data is how the kernel reports that its buffer overflowed.
                    _onError(new InternalBufferOverflowException("The directory change buffer overflowed."));
                }
                else
                {
                    Dispatch(_buffer.AsSpan(0, (int)returned));
                }

                if (_stopping)
                {
                    return;
                }

                IssueRead(out _);
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            _onError(ex);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> packet)
    {
        var allowed = _allowedTypes();
        var offset = 0;
        while (offset + HeaderBytes <= packet.Length)
        {
            var record = packet[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var action = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[56..]);
            var nameBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[80..]);
            Interlocked.Increment(ref _recordsSeen);

            if (action is ActionAdded or ActionModified or ActionRenamedNew &&
                (attributes & DirectoryAttribute) == 0 &&
                HeaderBytes + nameBytes <= record.Length)
            {
                var name = MemoryMarshal.Cast<byte, char>(record.Slice(HeaderBytes, nameBytes));
                var type = MediaExtensionFilter.Classify(name);
                if (type != WatchTypes.None && (type & allowed) != 0)
                {
                    // The only allocation on this path, and only for files that are candidates for optimisation.
                    var path = Path.Combine(_root, new string(name));
                    try
                    {
                        _onHit(path, type, action == ActionRenamedNew);
                    }
                    catch
                    {
                        // Never let a consumer exception unwind into the reader thread.
                    }
                }
            }

            if (next == 0)
            {
                break;
            }

            offset += (int)next;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            // Completes the outstanding read with ERROR_OPERATION_ABORTED, which wakes the reader thread.
            CancelIoEx(_directory, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or InvalidOperationException)
        {
            // handle already unusable
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _directory.Dispose();
        if (!_thread.IsAlive)
        {
            // Only release what the kernel may still write to once nothing can be reading from it.
            _pin.Free();
            Marshal.FreeHGlobal(_overlapped);
            _completed.Dispose();
            _firstRead.Dispose();
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadDirectoryChangesExW(
        SafeFileHandle directory,
        IntPtr buffer,
        uint bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        out uint bytesReturned,
        IntPtr overlapped,
        IntPtr completionRoutine,
        int informationClass);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle handle,
        IntPtr overlapped,
        out uint bytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}
