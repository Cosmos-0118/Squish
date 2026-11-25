using Squish.BackgroundService.Automation;
using Squish.CliBridge;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Xunit;

namespace CliBridge.Tests;

public class WatchSettingsTests
{
    private static CliFolderWatchSettings Create(WatchCommandOptions options, params string[] dirs) =>
        new(options, dirs, new TypeFilter(options.IncludeTypes, options.ExcludeTypes));

    [Fact]
    public void WatchesEveryTypeInEveryFolderWithoutLimits()
    {
        var config = Create(new WatchCommandOptions(), @"C:\a", @"C:\b").Current;

        Assert.All(new[] { ItemType.Image, ItemType.Video, ItemType.Pdf, ItemType.Document }, t => Assert.True(config.IsEnabled(t)));
        Assert.True(config.WithinSizeLimit(ItemType.Video, long.MaxValue / 4));
        Assert.Equal(0, config.JobLimit(ItemType.Image));
        Assert.False(config.Recursive);
        Assert.Equal(2, config.BuildPlan().Count);
    }

    [Fact]
    public void RecursiveFlagAndTypeFilterFlowIntoTheConfig()
    {
        var config = Create(new WatchCommandOptions { Recursive = true, IncludeTypes = new[] { "png", "mp4" }, ExcludeTypes = new[] { "mp4" } }, @"C:\a").Current;

        Assert.True(config.Recursive);
        Assert.True(config.IsExtensionAllowed("png"));
        Assert.False(config.IsExtensionAllowed("jpg"));
        Assert.False(config.IsExtensionAllowed("mp4"));
    }

    [Fact]
    public void RequestMetadataCarriesCliFlags()
    {
        var settings = Create(new WatchCommandOptions { Aggressive = true, RemoveAudio = true, PlaybackSpeedFactor = 2 }, @"C:\a");
        var metadata = new Dictionary<string, object?>();

        settings.ApplyRequestMetadata(ItemType.Video, FilePath.From(@"C:\a\x.mp4"), metadata);

        Assert.Equal("cli.watch", metadata["source"]);
        Assert.Equal(true, metadata["watch.aggressive"]);
        Assert.Equal(true, metadata["video.removeAudio"]);
        Assert.Equal(2.0, metadata["video.playbackSpeed"]);
    }
}
