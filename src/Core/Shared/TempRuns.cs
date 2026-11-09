using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Squish.Core.Shared;

/// <summary>
/// Per-run scratch directories under <c>%TEMP%\Squish\runs\&lt;pid&gt;-&lt;guid&gt;</c>. Every job that stages files (images, PDFs,
/// Office files) works in one directory of its own and deletes it whole, instead of scattering <c>squish-*</c> files across the
/// temp folder where a crash or a kill leaves them for ever. <see cref="SweepStale"/> clears what a dead process left behind:
/// the process id in the name says whose directory it was.
/// </summary>
public static class TempRuns
{
    /// <summary>Directories this old are swept even when the process that made them still exists (a recycled pid, or a leaked scope).</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(6);

    /// <summary>Loose <c>squish-*</c> entries from before per-run directories existed.</summary>
    public static readonly TimeSpan LegacyMaximumAge = TimeSpan.FromHours(24);

    public static string Root => Path.Combine(Path.GetTempPath(), "Squish", "runs");

    /// <summary>Creates a fresh, empty directory for one job and returns its path.</summary>
    public static string CreateDirectory()
    {
        var path = Path.Combine(Root, $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes a directory made by <see cref="CreateDirectory"/> with everything in it; never throws.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: something still has a file open. The next sweep removes it.
        }
    }

    /// <summary>
    /// Removes run directories whose process is gone or that are older than <see cref="MaximumAge"/>, and loose legacy
    /// <c>squish-*</c> temp entries older than <see cref="LegacyMaximumAge"/>. Returns how many entries were removed.
    /// </summary>
    public static int SweepStale(DateTime? utcNow = null, Func<int, bool>? isProcessAlive = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        isProcessAlive ??= IsProcessAlive;
        var removed = 0;

        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var directory in Directory.EnumerateDirectories(Root))
                {
                    if (ShouldSweep(directory, now, isProcessAlive) && TrySweepDirectory(directory))
                    {
                        removed++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process may be creating or removing entries as we enumerate; the next sweep catches the rest.
        }

        return removed + SweepLegacy(now);
    }

    internal static bool ShouldSweep(string directory, DateTime utcNow, Func<int, bool> isProcessAlive)
    {
        var name = Path.GetFileName(directory);
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0 && int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && !isProcessAlive(pid))
        {
            return true;
        }

        // Alive (or unparseable) owner: only an old, untouched directory is safe to call abandoned.
        return utcNow - Directory.GetLastWriteTimeUtc(directory) > MaximumAge;
    }

    private static int SweepLegacy(DateTime utcNow)
    {
        var removed = 0;
        try
        {
            var temp = Path.GetTempPath();
            foreach (var entry in Directory.EnumerateFileSystemEntries(temp, "squish-*"))
            {
                try
                {
                    var isDirectory = Directory.Exists(entry);
                    var written = isDirectory ? Directory.GetLastWriteTimeUtc(entry) : File.GetLastWriteTimeUtc(entry);
                    if (utcNow - written <= LegacyMaximumAge)
                    {
                        continue;
                    }

                    if (isDirectory)
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }

                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or not ours to delete; leave it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp folder itself was unreadable; nothing to sweep.
        }

        return removed;
    }

    private static bool TrySweepDirectory(string directory)
    {
        TryDelete(directory);
        return !Directory.Exists(directory);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Cannot tell (access denied, say): assume it lives and let the age rule decide.
            return true;
        }
    }
}
