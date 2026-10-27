using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Squish.Core.Optimizers;

namespace Squish.BackgroundService.Automation;

[Flags]
internal enum WatchTypes
{
    None = 0,
    Image = 1,
    Video = 2
}

internal readonly record struct WatchRootSpec(string Path, WatchTypes Types);

/// <summary>
/// Turns the configured image/video folder lists into the smallest set of OS watches that still covers them.
/// A folder listed for both kinds shares one watch, and a folder nested inside another watched folder that
/// already covers its types needs none of its own.
/// </summary>
internal static class WatchPlan
{
    public static IReadOnlyList<WatchRootSpec> Build(bool paused, bool images, bool videos, IEnumerable<string?> imageDirs, IEnumerable<string?> videoDirs)
    {
        if (paused)
        {
            return Array.Empty<WatchRootSpec>();
        }

        var merged = new Dictionary<string, WatchTypes>(StringComparer.OrdinalIgnoreCase);
        if (images)
        {
            Merge(merged, imageDirs, WatchTypes.Image);
        }

        if (videos)
        {
            Merge(merged, videoDirs, WatchTypes.Video);
        }

        var roots = merged.Select(kvp => new WatchRootSpec(kvp.Key, kvp.Value)).ToList();
        return roots
            .Where(root => !roots.Any(other => IsCoveredBy(root, other)))
            .OrderBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static WatchTypes TypesFor(ItemType type) => type switch
    {
        ItemType.Image => WatchTypes.Image,
        ItemType.Video => WatchTypes.Video,
        _ => WatchTypes.None
    };

    private static void Merge(Dictionary<string, WatchTypes> target, IEnumerable<string?> directories, WatchTypes type)
    {
        foreach (var candidate in directories)
        {
            var normalised = Normalise(candidate);
            if (normalised is null)
            {
                continue;
            }

            target[normalised] = target.TryGetValue(normalised, out var existing) ? existing | type : type;
        }
    }

    private static string? Normalise(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(candidate.Trim().Trim('"'));
            var root = Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsCoveredBy(WatchRootSpec inner, WatchRootSpec outer)
    {
        if (string.Equals(inner.Path, outer.Path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((inner.Types & ~outer.Types) != 0)
        {
            return false;
        }

        var prefix = Path.EndsInDirectorySeparator(outer.Path) ? outer.Path : outer.Path + Path.DirectorySeparatorChar;
        return inner.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
