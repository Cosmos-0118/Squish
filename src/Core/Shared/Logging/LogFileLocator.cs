using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Squish.Core.Shared.Logging;

internal static class LogFileLocator
{
    /// <summary>Daily log files older than this are deleted the first time a log path is resolved in a process.</summary>
    internal static readonly TimeSpan LogRetention = TimeSpan.FromDays(14);

    private static readonly object PruneGate = new();
    private static readonly HashSet<string> PrunedDirectories = new(StringComparer.OrdinalIgnoreCase);

    public static string GetLogFilePath(string componentName)
    {
        var logsDirectory = EnsureLogsDirectory();
        PruneOldLogsOnce(logsDirectory);
        var safeComponent = SanitizeComponent(componentName);
        var fileName = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-{1:yyyyMMdd}.log",
            safeComponent,
            DateTime.UtcNow);

        return Path.Combine(logsDirectory, fileName);
    }

    private static void PruneOldLogsOnce(string logsDirectory)
    {
        lock (PruneGate)
        {
            if (!PrunedDirectories.Add(logsDirectory))
            {
                return;
            }
        }

        PruneOldLogs(logsDirectory, DateTime.UtcNow - LogRetention);
    }

    internal static int PruneOldLogs(string logsDirectory, DateTime cutoffUtc)
    {
        var deleted = 0;
        try
        {
            // Only touch the daily files we write: <component>-yyyyMMdd.log
            foreach (var file in Directory.EnumerateFiles(logsDirectory, "*-????????.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch
                {
                    // in use by another process or read-only; try again next run
                }
            }
        }
        catch
        {
            // best effort
        }

        return deleted;
    }

    public static string EnsureLogsDirectory()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        var existingLogs = TryFindExistingLogsDirectory(baseDirectory);
        if (existingLogs is not null)
        {
            Directory.CreateDirectory(existingLogs);
            return existingLogs;
        }

        var repoRoot = TryFindRepositoryRoot(baseDirectory);
        if (repoRoot is not null)
        {
            var repoLogs = Path.Combine(repoRoot.FullName, "logs");
            Directory.CreateDirectory(repoLogs);
            return repoLogs;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Squish", "logs");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string? TryFindExistingLogsDirectory(DirectoryInfo start)
    {
        var current = start;
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "logs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static DirectoryInfo? TryFindRepositoryRoot(DirectoryInfo start)
    {
        var current = start;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Squish.sln")) || Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string SanitizeComponent(string componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName))
        {
            return "squish";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var buffer = new char[componentName.Length];
        var hasValidChar = false;

        for (var i = 0; i < componentName.Length; i++)
        {
            var current = componentName[i];
            if (current == ' ')
            {
                buffer[i] = '-';
                hasValidChar = true;
                continue;
            }

            var isInvalid = Array.IndexOf(invalidCharacters, current) >= 0;
            if (isInvalid)
            {
                buffer[i] = '-';
                continue;
            }

            buffer[i] = char.ToLowerInvariant(current);
            hasValidChar = true;
        }

        if (!hasValidChar)
        {
            return "squish";
        }

        var candidate = new string(buffer).Trim('-');
        return string.IsNullOrWhiteSpace(candidate) ? "squish" : candidate;
    }
}
