using System;
using System.IO;

namespace Squish.BackgroundService.Automation;

internal enum FileReadyState
{
    /// <summary>Nobody is writing the file and it has stopped changing.</summary>
    Ready,
    /// <summary>The path no longer holds a regular file.</summary>
    Gone,
    /// <summary>Another process holds the file open for writing (or the open failed transiently).</summary>
    Busy,
    /// <summary>Size or modification time moved since the last look, or the file was written moments ago.</summary>
    Unstable,
    /// <summary>Zero bytes: the writer has created the file but not produced content yet.</summary>
    Empty,
    /// <summary>A cloud placeholder (OneDrive and friends). Reading it would trigger a download, so never touch it.</summary>
    CloudPlaceholder
}

internal readonly record struct FileProbe(FileReadyState State, long Length, long LastWriteTicks);

/// <summary>
/// Decides whether a file has finished being written without ever blocking: one metadata call plus one
/// share-mode probe per look. Callers re-probe on a schedule instead of sleeping.
/// </summary>
internal static class FileReadiness
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <param name="path">Absolute path to inspect.</param>
    /// <param name="previousLength">Length seen by the previous probe, or a negative value for none.</param>
    /// <param name="previousTicks">Last-write ticks seen by the previous probe.</param>
    /// <param name="utcNow">Current time, injected for tests.</param>
    /// <param name="settle">A file untouched for at least this long is considered finished even on the first look.</param>
    public static FileProbe Probe(string path, long previousLength, long previousTicks, DateTime utcNow, TimeSpan settle)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FileProbe(FileReadyState.Gone, 0, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileProbe(FileReadyState.Gone, 0, 0);
        }

        long length;
        long ticks;
        FileAttributes attributes;
        try
        {
            attributes = info.Attributes;
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileProbe(FileReadyState.Busy, 0, 0);
        }

        if ((attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
        {
            return new FileProbe(FileReadyState.CloudPlaceholder, length, ticks);
        }

        if (length <= 0)
        {
            return new FileProbe(FileReadyState.Empty, length, ticks);
        }

        // Opening with FileShare.Read fails exactly when another handle has the file open for writing.
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.None);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileProbe(FileReadyState.Gone, length, ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileProbe(FileReadyState.Busy, length, ticks);
        }

        if (previousLength == length && previousTicks == ticks)
        {
            return new FileProbe(FileReadyState.Ready, length, ticks);
        }

        // Untouched for a while and nobody has it open for writing: finished. A modification time in the future
        // (clock skew on network shares) yields a negative age, so those wait for a second matching observation.
        var age = utcNow - new DateTime(ticks, DateTimeKind.Utc);
        return age >= settle
            ? new FileProbe(FileReadyState.Ready, length, ticks)
            : new FileProbe(FileReadyState.Unstable, length, ticks);
    }
}
