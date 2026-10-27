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
    public bool IsImage => ItemType == ItemType.Image;

    public bool IsVideo => ItemType == ItemType.Video;
}
