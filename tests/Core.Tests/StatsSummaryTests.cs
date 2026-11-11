using Squish.Core.Optimizers;
using Squish.Core.Stats;
using Xunit;

namespace Core.Tests;

public sealed class StatsSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static OptimisationRecord Record(int daysAgo, StatsKind kind, long original, long optimised, double seconds = 1) =>
        new(Now.AddDays(-daysAgo), kind, original, optimised, seconds);

    [Fact]
    public void TotalsAndKindsAreAggregated()
    {
        var records = new[]
        {
            Record(0, StatsKind.Image, 1000, 400),
            Record(0, StatsKind.Image, 2000, 1000),
            Record(1, StatsKind.Video, 10_000, 4_000, seconds: 30),
            Record(2, StatsKind.Pdf, 500, 600), // grew: counts as a file, saves nothing
        };

        var summary = StatsSummary.Build(records, StatsRange.Last7Days, Now, TimeZoneInfo.Utc);

        Assert.Equal(4, summary.Files);
        Assert.Equal(13_500, summary.OriginalBytes);
        Assert.Equal(600 + 1000 + 6000, summary.SavedBytes);
        Assert.Equal(TimeSpan.FromSeconds(33), summary.TimeSpent);

        Assert.Equal(2, summary.ByKind.Single(k => k.Kind == StatsKind.Image).Files);
        Assert.Equal(1, summary.ByKind.Single(k => k.Kind == StatsKind.Video).Files);
        Assert.Equal(1, summary.ByKind.Single(k => k.Kind == StatsKind.Pdf).Files);
        Assert.Equal(0, summary.ByKind.Single(k => k.Kind == StatsKind.Document).Files);
        Assert.Equal(0, summary.ByKind.Single(k => k.Kind == StatsKind.Pdf).SavedBytes);
    }

    [Fact]
    public void RangeLimitsRecordsAndBuildsOneBucketPerDay()
    {
        var records = new[]
        {
            Record(0, StatsKind.Image, 100, 50),
            Record(6, StatsKind.Image, 100, 50),
            Record(7, StatsKind.Image, 100, 50),   // outside 7 days
            Record(40, StatsKind.Image, 100, 50),  // outside 30 days
        };

        var week = StatsSummary.Build(records, StatsRange.Last7Days, Now, TimeZoneInfo.Utc);
        var month = StatsSummary.Build(records, StatsRange.Last30Days, Now, TimeZoneInfo.Utc);
        var all = StatsSummary.Build(records, StatsRange.AllTime, Now, TimeZoneInfo.Utc);

        Assert.Equal(2, week.Files);
        Assert.Equal(7, week.Days.Count);
        Assert.Equal(3, month.Files);
        Assert.Equal(30, month.Days.Count);
        Assert.Equal(4, all.Files);
        Assert.Equal(41, all.Days.Count);
        Assert.Equal(1, week.Days[^1].Files);
        Assert.Equal(Now.UtcDateTime.Date, week.Days[^1].Day);
    }

    [Fact]
    public void EmptyHistoryGivesZerosNotErrors()
    {
        var summary = StatsSummary.Build(Array.Empty<OptimisationRecord>(), StatsRange.AllTime, Now, TimeZoneInfo.Utc);

        Assert.Equal(0, summary.Files);
        Assert.Equal(0d, summary.SavedFraction);
        Assert.Equal(14, summary.Days.Count);
        Assert.Equal(4, summary.ByKind.Count);
    }

    [Fact]
    public void ClipboardItemsCountAsImagesAndVideos()
    {
        Assert.Equal(StatsKind.Image, OptimisationRecord.KindFor(ItemType.ClipboardImage));
        Assert.Equal(StatsKind.Video, OptimisationRecord.KindFor(ItemType.ClipboardVideo));
        Assert.Null(OptimisationRecord.KindFor(ItemType.Unknown));
    }

    [Fact]
    public void StorePersistsRecordsAndSkipsDamagedLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squish-stats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OptimisationStatsStore(dir);
            store.Append(Record(0, StatsKind.Video, 5000, 2000, 12.5));
            File.AppendAllText(Path.Combine(dir, "history.csv"), "garbage line\n1,99,1,1,1\n");
            store.Append(Record(1, StatsKind.Pdf, 300, 200));

            var loaded = new OptimisationStatsStore(dir).Load();

            Assert.Equal(2, loaded.Count);
            Assert.Equal(StatsKind.Video, loaded[0].Kind);
            Assert.Equal(5000, loaded[0].OriginalBytes);
            Assert.Equal(12.5, loaded[0].Seconds);
            Assert.Equal(StatsKind.Pdf, loaded[1].Kind);

            store.Clear();
            Assert.Empty(new OptimisationStatsStore(dir).Load());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
