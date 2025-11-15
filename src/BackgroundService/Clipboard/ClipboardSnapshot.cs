using System.Collections.Generic;

namespace ClopWindows.BackgroundService.Clipboard;

/// <summary>
/// Immutable representation of clipboard contents captured on the STA monitoring thread.
/// </summary>
public sealed record ClipboardSnapshot(
    IReadOnlyList<string> FilePaths,
    byte[]? ImageBytes,
    string? Text,
    bool HasOptimisationMarker)
{
    public static ClipboardSnapshot Empty { get; } = new(Array.Empty<string>(), null, null, false);

    public bool HasContent => (FilePaths.Count > 0) || (ImageBytes is { Length: > 0 }) || !string.IsNullOrWhiteSpace(Text);
}
