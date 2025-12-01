using System;
using System.Globalization;
using System.IO;

namespace ClopWindows.Core.Shared.Logging;

internal static class LogFileLocator
{
    public static string GetLogFilePath(string componentName)
    {
        var logsDirectory = EnsureLogsDirectory();
        var safeComponent = SanitizeComponent(componentName);
        var fileName = string.Format(
            CultureInfo.InvariantCulture,
            "{0}-{1:yyyyMMdd}.log",
            safeComponent,
            DateTime.UtcNow);

        return Path.Combine(logsDirectory, fileName);
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

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clop", "logs");
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
            if (File.Exists(Path.Combine(current.FullName, "ClopWindows.sln")) || Directory.Exists(Path.Combine(current.FullName, ".git")))
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
            return "clop";
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
            return "clop";
        }

        var candidate = new string(buffer).Trim('-');
        return string.IsNullOrWhiteSpace(candidate) ? "clop" : candidate;
    }
}
