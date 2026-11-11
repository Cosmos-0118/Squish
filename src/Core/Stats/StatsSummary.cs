namespace Squish.Core.Stats;

public enum StatsRange
{
    Last7Days,
    Last30Days,
    AllTime
}

public sealed record KindTotals(StatsKind Kind, int Files, long OriginalBytes, long SavedBytes);

public sealed record DayBucket(DateTime Day, int Files, long SavedBytes);

public sealed record StatsSummary(
    int Files,
    long OriginalBytes,
    long OptimisedBytes,
    long SavedBytes,
    TimeSpan TimeSpent,
    IReadOnlyList<KindTotals> ByKind,
    IReadOnlyList<DayBucket> Days)
{
    /// <summary>Share of the original size that was removed, 0–1 (0 when nothing is known).</summary>
    public double SavedFraction => OriginalBytes > 0 ? (double)SavedBytes / OriginalBytes : 0d;

    public static StatsSummary Build(IEnumerable<OptimisationRecord> records, StatsRange range, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var all = records as IReadOnlyCollection<OptimisationRecord> ?? records.ToList();
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var first = all.OrderBy(r => r.Timestamp).FirstOrDefault();

        var dayCount = range switch
        {
            StatsRange.Last7Days => 7,
            StatsRange.Last30Days => 30,
            // All time: chart from the first record, but never fewer than 14 or more than 90 bars so it stays readable.
            _ => first is null ? 14 : Math.Clamp((today - TimeZoneInfo.ConvertTime(first.Timestamp, zone).Date).Days + 1, 14, 90)
        };

        var start = today.AddDays(-(dayCount - 1));
        var days = Enumerable.Range(0, dayCount).ToDictionary(offset => start.AddDays(offset), _ => (Files: 0, Saved: 0L));

        var files = 0;
        long original = 0, optimised = 0, saved = 0;
        double seconds = 0;
        var kinds = Enum.GetValues<StatsKind>().ToDictionary(k => k, _ => (Files: 0, Original: 0L, Saved: 0L));

        foreach (var record in all)
        {
            var day = TimeZoneInfo.ConvertTime(record.Timestamp, zone).Date;
            if (range != StatsRange.AllTime && (day < start || day > today))
            {
                continue;
            }

            files++;
            original += record.OriginalBytes;
            optimised += record.OptimisedBytes;
            saved += record.SavedBytes;
            seconds += Math.Max(0, record.Seconds);

            var kind = kinds[record.Kind];
            kinds[record.Kind] = (kind.Files + 1, kind.Original + record.OriginalBytes, kind.Saved + record.SavedBytes);

            if (days.TryGetValue(day, out var bucket))
            {
                days[day] = (bucket.Files + 1, bucket.Saved + record.SavedBytes);
            }
        }

        return new StatsSummary(
            files,
            original,
            optimised,
            saved,
            TimeSpan.FromSeconds(seconds),
            kinds.Select(pair => new KindTotals(pair.Key, pair.Value.Files, pair.Value.Original, pair.Value.Saved)).ToList(),
            days.OrderBy(pair => pair.Key).Select(pair => new DayBucket(pair.Key, pair.Value.Files, pair.Value.Saved)).ToList());
    }
}
