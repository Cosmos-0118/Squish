using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Squish.Core.Shared;

/// <summary>
/// Persists lightweight optimisation markers alongside files (via NTFS alternate data streams) so we
/// can skip re-processing content that has already been optimised, even when the file keeps its
/// original name.
/// </summary>
public static class SquishOptimisationMarker
{
    private const string MarkerStreamName = "squish.optimised";
    private const string LegacyMarkerStreamName = "clop.optimised";
    private const string MarkerPrefix = "v1|";

    public static bool TryMark(FilePath path)
    {
        if (!SupportsMarkers || string.IsNullOrWhiteSpace(path.Value))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path.Value);
            if (!info.Exists)
            {
                return false;
            }

            var payload = string.Format(CultureInfo.InvariantCulture, "{0}{1}|{2}", MarkerPrefix, info.Length, info.LastWriteTimeUtc.Ticks);
            using var stream = new FileStream(BuildStreamPath(path.Value), FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false);
            writer.Write(payload);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool HasValidMarker(FilePath path)
    {
        if (!SupportsMarkers || string.IsNullOrWhiteSpace(path.Value))
        {
            return false;
        }

        return HasValidMarker(path, MarkerStreamName) || HasValidMarker(path, LegacyMarkerStreamName);
    }

    private static bool HasValidMarker(FilePath path, string streamName)
    {
        try
        {
            var info = new FileInfo(path.Value);
            if (!info.Exists)
            {
                return false;
            }

            using var stream = new FileStream(BuildStreamPath(path.Value, streamName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var payload = reader.ReadToEnd();
            if (!payload.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var remainder = payload.AsSpan(MarkerPrefix.Length);
            var separatorIndex = remainder.IndexOf('|');
            if (separatorIndex < 0)
            {
                return false;
            }

            var sizeSpan = remainder[..separatorIndex];
            var ticksSpan = remainder[(separatorIndex + 1)..];
            if (!long.TryParse(sizeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordedSize) ||
                !long.TryParse(ticksSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordedTicks))
            {
                return false;
            }

            if (recordedSize == info.Length && recordedTicks == info.LastWriteTimeUtc.Ticks)
            {
                return true;
            }

            TryClear(path, streamName);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    public static void TryClear(FilePath path)
    {
        if (!SupportsMarkers || string.IsNullOrWhiteSpace(path.Value))
        {
            return;
        }

        TryClear(path, MarkerStreamName);
        TryClear(path, LegacyMarkerStreamName);
    }

    private static void TryClear(FilePath path, string streamName)
    {
        try
        {
            File.Delete(BuildStreamPath(path.Value, streamName));
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static bool SupportsMarkers => OperatingSystem.IsWindows();

    private static string BuildStreamPath(string path, string streamName = MarkerStreamName) => string.Concat(path, ":", streamName);
}
