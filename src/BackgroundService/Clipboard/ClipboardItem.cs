using Squish.Core.Optimizers;
using Squish.Core.Shared;

namespace Squish.BackgroundService.Clipboard;

internal enum ClipboardOrigin
{
    FileDrop,
    Bitmap,
    TextPath
}

internal sealed record ClipboardItem(
    FilePath SourcePath,
    ItemType ItemType,
    ClipboardOrigin Origin,
    bool IsTemporary,
    bool ShouldCopyToClipboard,
    uint OriginSequence)
{
    /// <summary>Set for bitmaps taken straight from the clipboard; used to recognise repeats of the same image.</summary>
    public ClipboardImageEntry? ImageEntry { get; init; }

    public bool IsImage => ItemType == ItemType.Image;

    public bool IsVideo => ItemType == ItemType.Video;
}
