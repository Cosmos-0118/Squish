using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Squish.Core.Shared;

/// <summary>
/// Finds helper executables (cjpeg, oxipng, cwebp, ffmpeg, ...). Lookup order is: explicit path, environment
/// variable, the bundled <c>tools</c> folder next to the app, the per-user Squish bin folder, and finally rooted
/// <c>PATH</c> entries. The current directory is never searched - that is how binaries get planted.
/// </summary>
internal static class ExternalToolResolver
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxSearchDepth = 3;

    public static string FileName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <param name="tool">Base file name without extension, e.g. <c>cjpeg-static</c>.</param>
    /// <param name="toolFolder">Folder under <c>tools/</c> that fetch-tools.ps1 unpacks the tool into.</param>
    /// <param name="environmentVariable">Optional override variable, e.g. <c>SQUISH_CJPEG</c>.</param>
    /// <param name="configuredPath">Explicit path from settings, if any.</param>
    /// <param name="alternateNames">Other base names the same tool ships under (e.g. <c>cjpeg</c>).</param>
    public static string? Resolve(string tool, string toolFolder, string? environmentVariable = null, string? configuredPath = null, params string[] alternateNames)
    {
        var cacheKey = $"{tool}|{toolFolder}|{environmentVariable}|{configuredPath}";
        if (Cache.TryGetValue(cacheKey, out var cached) && (cached is null || File.Exists(cached)))
        {
            return cached;
        }

        var resolved = ResolveCore(tool, toolFolder, environmentVariable, configuredPath, alternateNames);
        if (resolved is not null)
        {
            Cache[cacheKey] = resolved;
        }
        else
        {
            Cache.TryRemove(cacheKey, out _);
        }

        return resolved;
    }

    /// <summary>Forgets cached lookups (used by tests that change environment variables).</summary>
    internal static void ResetCache() => Cache.Clear();

    private static string? ResolveCore(string tool, string toolFolder, string? environmentVariable, string? configuredPath, string[] alternateNames)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (Path.IsPathRooted(expanded) && File.Exists(expanded))
            {
                return expanded;
            }
        }

        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            var fromEnv = Environment.GetEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                var expanded = Environment.ExpandEnvironmentVariables(fromEnv.Trim().Trim('"'));
                if (Path.IsPathRooted(expanded) && File.Exists(expanded))
                {
                    return expanded;
                }
            }
        }

        var fileNames = new[] { tool }.Concat(alternateNames).Select(FileName).ToArray();

        foreach (var root in EnumerateBundledRoots(toolFolder))
        {
            var found = FindInTree(root, fileNames, MaxSearchDepth);
            if (found is not null)
            {
                return found;
            }
        }

        foreach (var root in EnumerateUserRoots())
        {
            var found = FindInTree(root, fileNames, 1);
            if (found is not null)
            {
                return found;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathRooted(segment))
            {
                continue;
            }

            foreach (var name in fileNames)
            {
                try
                {
                    var probe = Path.Combine(segment, name);
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

    private static IEnumerable<string> EnumerateBundledRoots(string toolFolder)
    {
        // Explicit tools root (CI, portable installs, developers staging tools outside the app folder).
        var toolsRoot = Environment.GetEnvironmentVariable("SQUISH_TOOLS_DIR");
        if (!string.IsNullOrWhiteSpace(toolsRoot) && Path.IsPathRooted(toolsRoot))
        {
            var candidate = Path.Combine(toolsRoot.Trim().Trim('"'), toolFolder);
            if (Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }

        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            yield break;
        }

        foreach (var relative in new[]
                 {
                     new[] { "tools", toolFolder },
                     new[] { "..", "tools", toolFolder },
                     new[] { "..", "..", "tools", toolFolder }
                 })
        {
            var candidate = ToolLocator.SafeCombine(baseDir, relative);
            if (candidate is not null && Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateUserRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "Squish", "bin");
            yield return Path.Combine(local, "Squish", "bin", "x64");
        }
    }

    private static string? FindInTree(string root, string[] fileNames, int depth)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            foreach (var name in fileNames)
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct))
                {
                    return direct;
                }
            }

            if (depth <= 0)
            {
                return null;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var nested = FindInTree(directory, fileNames, depth - 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // unreadable folder - keep looking elsewhere
        }

        return null;
    }
}
