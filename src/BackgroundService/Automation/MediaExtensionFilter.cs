using System;
using System.Collections.Frozen;
using System.IO;
using Squish.Core.Shared;

namespace Squish.BackgroundService.Automation;

/// <summary>
/// Allocation-free extension classification for the file-system event callback, which runs on the OS watcher
/// thread and must stay far cheaper than the rate at which the kernel produces events.
/// </summary>
internal static class MediaExtensionFilter
{
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> ImageLookup =
        MediaFormats.ImageExtensionNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> VideoLookup =
        MediaFormats.VideoExtensionNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

    public static WatchTypes Classify(ReadOnlySpan<char> path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length < 2)
        {
            return WatchTypes.None;
        }

        extension = extension[1..];
        if (ImageLookup.Contains(extension))
        {
            return WatchTypes.Image;
        }

        return VideoLookup.Contains(extension) ? WatchTypes.Video : WatchTypes.None;
    }
}
