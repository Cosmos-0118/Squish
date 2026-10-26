using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClopWindows.Core.Shared;

internal static class ToolLocator
{
    private static readonly string[] RepoFallbackSegments = { "..", "..", "..", "..", ".." };
    private static readonly string[] AssemblyFallbackSegments = { "..", ".." };

    public static IEnumerable<string> EnumeratePossibleFiles(string? baseDirectory, string[] relativeSegments)
    {
        foreach (var candidate in EnumerateCandidatePaths(baseDirectory, relativeSegments))
        {
            if (candidate is not null && File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    public static IEnumerable<string> EnumeratePossibleDirectories(string? baseDirectory, string[] relativeSegments)
    {
        foreach (var candidate in EnumerateCandidatePaths(baseDirectory, relativeSegments))
        {
            if (candidate is not null && Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    public static string? SafeCombine(string root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            var combined = Path.Combine(new[] { root }.Concat(segments).ToArray());
            return Path.GetFullPath(combined);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves <paramref name="command"/> against rooted <c>PATH</c> entries. The current directory (and any
    /// relative <c>PATH</c> entry) is deliberately ignored so a file dropped next to the user's documents can
    /// never shadow a real tool. On Windows <c>.exe</c>/<c>.com</c> are tried when no extension is given.
    /// </summary>
    public static string? ResolveOnPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        if (command.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
        {
            // Relative paths such as ".\\tool.exe" are never resolved implicitly.
            return null;
        }

        var candidates = new List<string> { command };
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(command)))
        {
            candidates.Add(command + ".exe");
            candidates.Add(command + ".com");
        }

        var environmentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in environmentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathRooted(segment))
            {
                continue;
            }

            foreach (var name in candidates)
            {
                try
                {
                    var probe = Path.Combine(segment.Trim('"'), name);
                    if (File.Exists(probe))
                    {
                        return probe;
                    }
                }
                catch (ArgumentException)
                {
                    // malformed PATH entry
                }
            }
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateCandidatePaths(string? baseDirectory, string[] relativeSegments)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            yield return SafeCombine(baseDirectory!, relativeSegments);
            yield return SafeCombine(baseDirectory!, RepoFallbackSegments.Concat(relativeSegments).ToArray());
        }

        var executingDir = Path.GetDirectoryName(typeof(ToolLocator).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(executingDir))
        {
            yield return SafeCombine(executingDir!, relativeSegments);
            yield return SafeCombine(executingDir!, AssemblyFallbackSegments.Concat(relativeSegments).ToArray());
        }
    }
}
