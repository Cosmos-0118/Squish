using Squish.Core.Optimizers;
using Xunit;

namespace Core.Tests;

public sealed class VideoEarlyOutTests
{
    private const double SourceKbps = 4000;
    private static readonly double DefaultRatio = new VideoOptimiserOptions().QualitySearchSkipRatio;

    [Fact]
    public void FrameDecimationAloneDoesNotCountAsAFilterThatBlocksTheEfficientSourceCheck()
    {
        Assert.False(VideoOptimiserPlan.HasFiltersBeyondDecimation(Array.Empty<string>()));
        Assert.False(VideoOptimiserPlan.HasFiltersBeyondDecimation(new[] { "mpdecimate=hi=64:lo=15:max=15" }));
    }

    [Theory]
    [InlineData("scale=1280:-2")]
    [InlineData("fps=30")]
    [InlineData("setpts=0.5*PTS")]
    public void ScalingAndFpsFiltersStillBlockTheEfficientSourceCheck(string filter)
    {
        Assert.True(VideoOptimiserPlan.HasFiltersBeyondDecimation(new[] { filter }));
        Assert.True(VideoOptimiserPlan.HasFiltersBeyondDecimation(new[] { "mpdecimate=hi=64:lo=15:max=15", filter }));
    }

    [Fact]
    public void DefaultSkipRatioIs1Point5()
    {
        // The sample overestimates the real encode by roughly 1.1-1.7x; see VideoOptimiserOptions.QualitySearchSkipRatio.
        Assert.Equal(1.5, DefaultRatio);
    }

    [Theory]
    [InlineData(1.00, false)]
    [InlineData(1.30, false)]
    [InlineData(1.49, false)]
    [InlineData(1.50, true)]   // boundary is inclusive
    [InlineData(1.51, true)]
    [InlineData(1.70, true)]
    [InlineData(3.70, true)]
    public void SkipsOnlyWhenTheSampledBitrateReachesTheRatio(double sampledOverSource, bool expected)
        => Assert.Equal(expected, VideoOptimiser.IsSampledBitrateTooHigh(SourceKbps * sampledOverSource, SourceKbps, DefaultRatio));

    [Fact]
    public void NeverSkipsWithoutAKnownSourceBitrate()
        => Assert.False(VideoOptimiser.IsSampledBitrateTooHigh(10_000, 0, DefaultRatio));

    // Measured on real footage (sampled bitrate / source bitrate, and what a full encode really achieved at the
    // default quality target). Clips that compress must never be skipped; clips that cannot shrink may be.
    [Theory]
    [InlineData("Big_Buck_Bunny_1080", 4152, 4185, 0.36, false)]
    [InlineData("Big_Buck_Bunny_720", 1142, 1579, 0.57, false)]
    [InlineData("sample-10s", 5292, 4207, 0.04, false)]
    [InlineData("Sintel_720", 2075, 4191, 0.60, false)]
    [InlineData("Jellyfish_1080", 7242, 4186, 0.00, true)]
    [InlineData("sample-15s", 22283, 6021, 0.00, true)]
    public void MeasuredClipsAreSkippedOnlyWhenTheyCouldNotHaveShrunk(string clip, double sampledKbps, double sourceKbps, double realSaving, bool expectSkip)
    {
        var skipped = VideoOptimiser.IsSampledBitrateTooHigh(sampledKbps, sourceKbps, DefaultRatio);

        Assert.True(expectSkip == skipped, $"{clip}: skipped={skipped}");
        if (skipped)
        {
            Assert.True(realSaving < 0.05, $"{clip} would have saved {realSaving:P0} but was skipped");
        }
    }
}
