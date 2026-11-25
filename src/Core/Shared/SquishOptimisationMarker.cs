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
    private const string MarkerPrefixV2 = "v2|";

    public static bool TryMark(FilePath path)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return false;
        }

        // The stream marker lives on the file itself; the index covers volumes and tools that lose it.
        var stream = SupportsMarkers && TryWriteStreamMarker(path);
        var indexed = OptimisedIndex.Default.Record(path);
        return stream || indexed;
    }

    private static bool TryWriteStreamMarker(FilePath path)
    {

        try
        {
            var info = new FileInfo(path.Value);
            if (!info.Exists)
            {
                return false;
            }

            // v2 also records a content fingerprint, so the marker survives tools that touch the timestamp (cloud sync, SMB rounding).
            var payload = FileFingerprint.TryCreate(path, out var fingerprint) && fingerprint.Length == info.Length
                ? string.Format(CultureInfo.InvariantCulture, "{0}{1}|{2}|{3}", MarkerPrefixV2, info.Length, info.LastWriteTimeUtc.Ticks, fingerprint.Hash)
                : string.Format(CultureInfo.InvariantCulture, "{0}{1}|{2}", MarkerPrefix, info.Length, info.LastWriteTimeUtc.Ticks);
            using (var stream = new FileStream(BuildStreamPath(path.Value), FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(payload);
            }

            // On NTFS, writing an alternate data stream bumps the file's last-write time. The marker records that time
            // (and is only valid while it matches), so without this it would invalidate itself the moment it was written,
            // and it would also undo "preserve original timestamps".
            File.SetLastWriteTimeUtc(path.Value, info.LastWriteTimeUtc);
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
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return false;
        }

        if (OptimisedIndex.Default.IsKnown(path))
        {
            return true;
        }

        return SupportsMarkers && (HasValidMarker(path, MarkerStreamName) || HasValidMarker(path, LegacyMarkerStreamName));
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
            var isV2 = payload.StartsWith(MarkerPrefixV2, StringComparison.Ordinal);
            if (!isV2 && !payload.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var fields = payload.Substring(MarkerPrefix.Length).Split('|');
            if (fields.Length < (isV2 ? 3 : 2) ||
                !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordedSize) ||
                !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordedTicks))
            {
                return false;
            }

            if (recordedSize == info.Length && recordedTicks == info.LastWriteTimeUtc.Ticks)
            {
                return true;
            }

            // Same size, new timestamp: only the metadata changed if the content still matches. Refresh the marker in place.
            if (isV2 && recordedSize == info.Length &&
                FileFingerprint.TryCreate(path, out var current) &&
                string.Equals(current.Hash, fields[2], StringComparison.OrdinalIgnoreCase))
            {
                return TryMark(path);
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
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            return;
        }

        OptimisedIndex.Default.Forget(path);
        if (!SupportsMarkers)
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
