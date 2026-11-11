using Squish.Core.Optimizers;

namespace Squish.Core.Stats;

/// <summary>The four kinds of file the summary page counts. Clipboard images and videos count as images and videos.</summary>
public enum StatsKind
{
    Image = 0,
    Video = 1,
    Pdf = 2,
    Document = 3
}

/// <summary>One successfully optimised file. Sizes are in bytes; <see cref="OriginalBytes"/> is 0 when it could not be read.</summary>
public sealed record OptimisationRecord(
    DateTimeOffset Timestamp,
    StatsKind Kind,
    long OriginalBytes,
    long OptimisedBytes,
    double Seconds)
{
    /// <summary>Bytes removed; never negative, so a file that grew does not eat into the total.</summary>
    public long SavedBytes => OriginalBytes > 0 && OptimisedBytes >= 0 ? Math.Max(0, OriginalBytes - OptimisedBytes) : 0;

    public static StatsKind? KindFor(ItemType type) => type switch
    {
        ItemType.Image or ItemType.ClipboardImage => StatsKind.Image,
        ItemType.Video or ItemType.ClipboardVideo => StatsKind.Video,
        ItemType.Pdf => StatsKind.Pdf,
        ItemType.Document => StatsKind.Document,
        _ => null
    };
}
