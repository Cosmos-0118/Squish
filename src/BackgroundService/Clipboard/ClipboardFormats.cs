namespace ClopWindows.BackgroundService.Clipboard;

internal static class ClipboardFormats
{
    /// <summary>
    /// Custom clipboard marker added to prevent re-processing items created by Clop itself.
    /// Matches the macOS pasteboard type for parity.
    /// </summary>
    public const string OptimisationStatus = "clop.optimisation.status";
}
