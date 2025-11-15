using System;
using System.Collections.Generic;
using System.IO;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;
using Microsoft.Extensions.Logging;

namespace ClopWindows.BackgroundService.Automation;

internal static class AutomationTargetResolver
{
    public static IReadOnlyList<AutomationTarget> ResolveTargets(
        AutomationOptimisePayload payload,
        ILogger logger,
        Func<FilePath, bool>? additionalPredicate = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(logger);

        var results = new List<AutomationTarget>();
        if (payload.Paths.Count == 0)
        {
            return results;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filter = new AutomationTypeFilter(payload.IncludeTypes, payload.ExcludeTypes);
        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = payload.Recursive
        };

        foreach (var path in payload.Paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var expanded = ExpandPath(path);
            if (string.IsNullOrWhiteSpace(expanded))
            {
                continue;
            }

            if (Directory.Exists(expanded))
            {
                foreach (var file in Directory.EnumerateFiles(expanded, "*", enumerationOptions))
                {
                    AddCandidate(file);
                }
            }
            else if (File.Exists(expanded))
            {
                AddCandidate(expanded);
            }
            else
            {
                logger.LogDebug("Automation request skipped missing item '{Item}'.", path);
            }
        }

        return results;

        void AddCandidate(string candidate)
        {
            if (!seen.Add(candidate))
            {
                return;
            }

            AutomationTarget? target = null;
            try
            {
                var filePath = FilePath.From(candidate);
                if (IsWithinWorkRoot(filePath))
                {
                    return;
                }

                if (additionalPredicate is not null && !additionalPredicate(filePath))
                {
                    return;
                }

                if (!filter.Allows(filePath.Extension))
                {
                    return;
                }

                var type = DetermineItemType(filePath);
                if (type is null)
                {
                    return;
                }

                target = new AutomationTarget(filePath, type.Value);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to process automation candidate {Candidate}.", candidate);
            }

            if (target is not null)
            {
                results.Add(target.Value);
            }
        }
    }

    private static string ExpandPath(string input)
    {
        var value = input.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = Environment.ExpandEnvironmentVariables(value);
        if (value.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = Path.Combine(home, value[2..]);
        }

        return Path.GetFullPath(value);
    }

    private static bool IsWithinWorkRoot(FilePath path)
    {
        var workRoot = ClopPaths.WorkRoot.Value;
        if (path.Value.Equals(workRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Path.EndsInDirectorySeparator(workRoot))
        {
            workRoot += Path.DirectorySeparatorChar;
        }

        return path.Value.StartsWith(workRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static ItemType? DetermineItemType(FilePath path)
    {
        if (MediaFormats.IsImage(path))
        {
            return ItemType.Image;
        }

        if (MediaFormats.IsVideo(path))
        {
            return ItemType.Video;
        }

        if (MediaFormats.IsPdf(path))
        {
            return ItemType.Pdf;
        }

        return null;
    }
}

internal readonly record struct AutomationTarget(FilePath Path, ItemType Type);

internal record AutomationOptimisePayload
{
    public List<string> Paths { get; init; } = new();
    public bool Recursive { get; init; }
    public bool Aggressive { get; init; }
    public bool RemoveAudio { get; init; }
    public double? PlaybackSpeedFactor { get; init; }
    public List<string> IncludeTypes { get; init; } = new();
    public List<string> ExcludeTypes { get; init; } = new();
}

internal sealed class AutomationTypeFilter
{
    private readonly HashSet<string>? _allowed;
    private readonly HashSet<string> _excluded;

    public AutomationTypeFilter(IEnumerable<string> include, IEnumerable<string> exclude)
    {
        _allowed = BuildSet(include);
        _excluded = BuildSet(exclude) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public bool Allows(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        extension = extension.TrimStart('.');
        if (_excluded.Contains(extension))
        {
            return false;
        }

        if (_allowed is null)
        {
            return true;
        }

        return _allowed.Contains(extension);
    }

    private static HashSet<string>? BuildSet(IEnumerable<string> values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var token in ExpandToken(value))
            {
                set.Add(token);
            }
        }

        return set.Count == 0 ? null : set;
    }

    private static IEnumerable<string> ExpandToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            yield break;
        }

        var trimmed = token.Trim().TrimStart('.');
        if (trimmed.Contains(',', StringComparison.Ordinal))
        {
            foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var expanded in ExpandToken(part))
                {
                    yield return expanded;
                }
            }
            yield break;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "image":
            case "images":
                foreach (var ext in MediaFormats.ImageExtensionNames)
                {
                    yield return ext;
                }
                yield break;
            case "video":
            case "videos":
                foreach (var ext in MediaFormats.VideoExtensionNames)
                {
                    yield return ext;
                }
                yield break;
            case "pdf":
            case "pdfs":
                foreach (var ext in MediaFormats.PdfExtensionNames)
                {
                    yield return ext;
                }
                yield break;
        }

        yield return trimmed;
    }
}
