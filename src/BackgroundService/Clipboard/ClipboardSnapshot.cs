using System.Collections.Generic;

namespace Squish.BackgroundService.Clipboard;

/// <summary>
/// Immutable representation of clipboard contents captured on the STA monitoring thread.
/// </summary>
/// <param name="Sequence">The Windows clipboard sequence number the snapshot was taken at.</param>
public sealed record ClipboardSnapshot(
    IReadOnlyList<string> FilePaths,
    byte[]? ImageBytes,
    string? Text,
    bool HasOptimisationMarker,
    uint Sequence = 0)
{
    public static ClipboardSnapshot Empty { get; } = new(Array.Empty<string>(), null, null, false);

    public bool HasContent => (FilePaths.Count > 0) || (ImageBytes is { Length: > 0 }) || !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Which parts of the clipboard a snapshot should include. Reading an image means decoding and re-encoding it, so
/// the monitor only does that work when the optimiser is actually going to use it.
/// </summary>
public sealed record ClipboardCaptureOptions(bool Files = true, bool Image = true, bool Text = true)
{
    public static ClipboardCaptureOptions All { get; } = new();
}

/// <summary>Counters exposed for diagnostics and benchmarks.</summary>
public readonly record struct ClipboardMonitorStats(
    long Notifications,
    long Captures,
    long SkippedDuplicates,
    long CaptureRetries,
    long CaptureFailures,
    long MissedNotifications,
    long PumpRestarts,
    bool UsingListener);
