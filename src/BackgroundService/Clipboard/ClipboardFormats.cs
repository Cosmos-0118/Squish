namespace Squish.BackgroundService.Clipboard;

internal static class ClipboardFormats
{
    /// <summary>
    /// Custom clipboard marker added to prevent re-processing items created by Squish itself.
    /// Matches the macOS pasteboard type for parity.
    /// </summary>
    public const string OptimisationStatus = "squish.optimisation.status";
}
