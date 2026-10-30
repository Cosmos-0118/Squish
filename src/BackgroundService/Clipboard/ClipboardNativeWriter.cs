using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;

namespace Squish.BackgroundService.Clipboard;

/// <summary>
/// Puts every format on the clipboard in a single Open/Empty/Set/Close.
/// <see cref="Clipboard.SetDataObject(object, bool)"/> with <c>copy: true</c> calls OleSetClipboard and then
/// OleFlushClipboard. Each call is its own clipboard generation, and Win+V stores both, so one optimised image
/// showed up twice.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ClipboardNativeWriter
{
    internal const uint CfDib = 8;
    internal const uint CfUnicodeText = 13;
    internal const uint CfHdrop = 15;

    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const int DropFilesHeaderBytes = 20;

    private static readonly ConcurrentDictionary<string, uint> RegisteredFormats = new(StringComparer.OrdinalIgnoreCase);

    internal static List<(uint Format, byte[] Data)> BuildPayloads(DataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var payloads = new List<(uint Format, byte[] Data)>();

        if (TryGetPng(data, out var png))
        {
            payloads.Add((FormatId("PNG"), png));
        }

        try
        {
            if (TryRenderDib(data) is { Length: > 0 } dib)
            {
                payloads.Add((CfDib, dib));
            }
        }
        catch (Exception ex) when (payloads.Count > 0 && ex is ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            // A PNG is already queued. Paint wants CF_DIB, but a failed DIB encode must not drop the image.
        }

        if (data.GetDataPresent(DataFormats.FileDrop, false) &&
            data.GetData(DataFormats.FileDrop, false) is string[] files &&
            files.Length > 0)
        {
            payloads.Add((CfHdrop, ToFileDrop(files)));
        }

        if (TryGetText(data) is { Length: > 0 } text)
        {
            payloads.Add((CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0")));
        }

        foreach (var format in data.GetFormats(false))
        {
            if (string.IsNullOrWhiteSpace(format) || IsStandardFormat(format))
            {
                continue;
            }

            if (PayloadBytes(data.GetData(format, false)) is not { Length: > 0 } bytes)
            {
                continue;
            }

            var id = FormatId(format);
            if (payloads.Exists(payload => payload.Format == id))
            {
                continue;
            }

            payloads.Add((id, bytes));
        }

        if (payloads.Count == 0)
        {
            throw new InvalidOperationException("Clipboard data contained no formats.");
        }

        return payloads;
    }

    /// <summary>
    /// Publishes <paramref name="payloads"/> as one clipboard change. Retries only while the clipboard cannot be
    /// opened; a failure after <c>EmptyClipboard</c> is not retried, because that would be a second history entry.
    /// </summary>
    internal static void Place(IReadOnlyList<(uint Format, byte[] Data)> payloads, IntPtr owner)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (payloads.Count == 0)
        {
            throw new InvalidOperationException("Clipboard data contained no formats.");
        }

        const int retries = 8;
        for (var attempt = 0; ; attempt++)
        {
            if (TryOpen(owner))
            {
                try
                {
                    WriteOpenClipboard(payloads);
                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (attempt >= retries)
            {
                throw new ExternalException("Could not open the clipboard.");
            }

            Thread.Sleep(40);
        }
    }

    internal static byte[] ToFileDrop(IReadOnlyList<string> paths)
    {
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            builder.Append(path);
            builder.Append('\0');
        }

        if (builder.Length == 0)
        {
            throw new InvalidOperationException("File drop list was empty.");
        }

        builder.Append('\0');
        var pathBytes = Encoding.Unicode.GetBytes(builder.ToString());
        var result = new byte[DropFilesHeaderBytes + pathBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0), DropFilesHeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), 1);
        pathBytes.CopyTo(result.AsSpan(DropFilesHeaderBytes));
        return result;
    }

    private static void WriteOpenClipboard(IReadOnlyList<(uint Format, byte[] Data)> payloads)
    {
        if (!EmptyClipboard())
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        foreach (var (format, bytes) in payloads)
        {
            var handle = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var destination = GlobalLock(handle);
                if (destination == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    Marshal.Copy(bytes, 0, destination, bytes.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(format, handle) == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                handle = IntPtr.Zero;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    GlobalFree(handle);
                }
            }
        }
    }

    private static bool TryOpen(IntPtr owner)
    {
        if (owner != IntPtr.Zero && OpenClipboard(owner))
        {
            return true;
        }

        return OpenClipboard(IntPtr.Zero);
    }

    private static bool TryGetPng(DataObject data, out byte[] bytes)
    {
        bytes = [];
        if (!data.GetDataPresent("PNG", false))
        {
            return false;
        }

        switch (data.GetData("PNG", false))
        {
            case byte[] raw when raw.Length > 0:
                bytes = raw;
                return true;
            case MemoryStream stream when stream.Length > 0:
                bytes = stream.ToArray();
                return true;
            default:
                return false;
        }
    }

    private static byte[]? TryRenderDib(DataObject data)
    {
        if (!data.GetDataPresent(DataFormats.Bitmap, false) ||
            data.GetData(DataFormats.Bitmap, false) is not Image image)
        {
            return null;
        }

        var owned = image as Bitmap;
        var bitmap = owned ?? new Bitmap(image);
        try
        {
            return ToDib(bitmap);
        }
        finally
        {
            if (owned is null)
            {
                bitmap.Dispose();
            }
        }
    }

    private static byte[] ToDib(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        if (stream.Length <= 14)
        {
            throw new InvalidOperationException("Bitmap encoding failed.");
        }

        var dib = new byte[stream.Length - 14];
        Buffer.BlockCopy(stream.GetBuffer(), 14, dib, 0, dib.Length);
        return dib;
    }

    private static string? TryGetText(DataObject data)
    {
        if (data.GetDataPresent(DataFormats.UnicodeText, false))
        {
            return data.GetData(DataFormats.UnicodeText, false) as string;
        }

        if (data.GetDataPresent(DataFormats.Text, false))
        {
            return data.GetData(DataFormats.Text, false) as string;
        }

        if (data.GetDataPresent(DataFormats.StringFormat, false))
        {
            return data.GetData(DataFormats.StringFormat, false) as string;
        }

        return null;
    }

    private static byte[]? PayloadBytes(object? value) => value switch
    {
        string text when text.Length > 0 => Encoding.Unicode.GetBytes(text + "\0"),
        byte[] bytes when bytes.Length > 0 => bytes,
        MemoryStream stream when stream.Length > 0 => stream.ToArray(),
        _ => null
    };

    private static bool IsStandardFormat(string format) =>
        format.Equals(DataFormats.Text, StringComparison.OrdinalIgnoreCase) ||
        format.Equals(DataFormats.UnicodeText, StringComparison.OrdinalIgnoreCase) ||
        format.Equals(DataFormats.OemText, StringComparison.OrdinalIgnoreCase) ||
        format.Equals(DataFormats.StringFormat, StringComparison.OrdinalIgnoreCase) ||
        format.Equals(DataFormats.Bitmap, StringComparison.OrdinalIgnoreCase) ||
        format.Equals(DataFormats.Dib, StringComparison.OrdinalIgnoreCase) ||
        format.Equals(DataFormats.FileDrop, StringComparison.OrdinalIgnoreCase) ||
        format.Equals("PNG", StringComparison.OrdinalIgnoreCase);

    private static uint FormatId(string name) => RegisteredFormats.GetOrAdd(name, static format =>
    {
        var id = RegisterClipboardFormat(format);
        if (id == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return id;
    });

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hwnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
