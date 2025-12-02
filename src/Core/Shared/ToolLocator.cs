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

    public static string? ResolveOnPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command) && File.Exists(command))
        {
            return command;
        }

        var environmentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in environmentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var probe = Path.Combine(segment, command);
            if (File.Exists(probe))
            {
                return probe;
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
