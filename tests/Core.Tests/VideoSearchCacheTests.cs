using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>Frame counting for the sampled windows and the per-job cache the video searches share.</summary>
public sealed class VideoSearchCacheTests
{
    // What ffmpeg 9 prints (at -loglevel verbose) for the two-output counting graph.
    private const string CountLog = """
        [out#0/null @ 000001f741d5a600] video:0KiB audio:0KiB subtitle:0KiB other streams:0KiB global headers:0KiB muxing overhead: unknown
        Output stream #0:0 (video): 38 frames encoded; 38 packets muxed (16112 bytes);
        Output stream #1:0 (video): 60 frames encoded; 60 packets muxed (25440 bytes);
        Input stream #0:0 (video): 85 packets read (2599470 bytes); 64 frames decoded; 0 decode errors;
        """;

    [Fact]
    public void FrameCountsComeFromTheTwoOutputsNotFromTheDecoder()
    {
        // 64 frames were decoded (the seek starts at the previous keyframe), but the window only has 60.
        var counts = FfmpegVideoQualitySearch.ParseOutputFrameCounts(CountLog);

        Assert.Equal((38, 60), counts);
    }

    [Fact]
    public void FrameCountsAreUnknownWhenALineIsMissingOrTheWindowIsEmpty()
    {
        Assert.Null(FfmpegVideoQualitySearch.ParseOutputFrameCounts(string.Empty));
        Assert.Null(FfmpegVideoQualitySearch.ParseOutputFrameCounts("Output stream #0:0 (video): 38 frames encoded;"));
        Assert.Null(FfmpegVideoQualitySearch.ParseOutputFrameCounts("Output stream #0:0 (video): 0 frames encoded;\nOutput stream #1:0 (video): 0 frames encoded;"));
    }

    [Fact]
    public void CountsAreRememberedPerWindowAndFilterChain()
    {
        using var cache = new VideoSearchCache();

        Assert.False(cache.TryGetCounts("a", out _));
        cache.StoreCounts("a", (10, 20));

        Assert.True(cache.TryGetCounts("a", out var counts));
        Assert.Equal((10, 20), counts);
        Assert.False(cache.TryGetCounts("b", out _));
    }

    private static FfmpegVideoQualitySearch.SampleReference Reference()
    {
        var pixels = new byte[64 * 64 * 4];
        Array.Fill(pixels, (byte)255);
        var tiles = TileReference.Create(new RasterImage(64, 64, pixels));
        tiles.EnableCaching();
        return new FfmpegVideoQualitySearch.SampleReference(new FfmpegVideoQualitySearch.SampleWindow(0, 1), new[] { 0 }, new[] { tiles }, 64, 64);
    }

    [Fact]
    public void ReferencesAreOnlyHandedBackForTheSameKeyAndReplacingThemReleasesTheOldOnes()
    {
        using var cache = new VideoSearchCache();
        var first = new List<FfmpegVideoQualitySearch.SampleReference> { Reference() };
        var second = new List<FfmpegVideoQualitySearch.SampleReference> { Reference() };

        cache.StoreReferences("clip|mpdecimate", first);

        Assert.Same(first, cache.TryGetReferences("clip|mpdecimate"));
        Assert.Null(cache.TryGetReferences("clip|mpdecimate,scale"));

        cache.StoreReferences("clip|scale", second);

        Assert.Null(cache.TryGetReferences("clip|mpdecimate"));
        Assert.Same(second, cache.TryGetReferences("clip|scale"));
    }

    [Fact]
    public void DisposingTheCacheDropsItsReferencesAndIsIdempotent()
    {
        var cache = new VideoSearchCache();
        cache.StoreReferences("k", new List<FfmpegVideoQualitySearch.SampleReference> { Reference() });

        cache.Dispose();
        cache.Dispose();

        Assert.Null(cache.TryGetReferences("k"));
    }
}

/// <summary>Counting and cache reuse against a real ffmpeg (skipped when none is installed).</summary>
[Collection("VideoTools")]
public sealed class VideoSearchCacheRealFfmpegTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "squish-search-cache-" + Guid.NewGuid().ToString("N"));

    public VideoSearchCacheRealFfmpegTests(VideoToolchainFixture tools)
    {
        _ = tools ?? throw new ArgumentNullException(nameof(tools));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static async Task Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
    }

    private sealed class CapturingToolchain : IVideoToolchain
    {
        public List<VideoOptimiserPlan> Plans { get; } = new();

        public Task<ToolchainResult> TranscodeAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            Plans.Add(plan);
            File.WriteAllBytes(tempOutput.Value, new byte[256]);
            return Task.FromResult(ToolchainResult.Successful());
        }

        public Task<ToolchainResult> ConvertToAnimatedAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(ToolchainResult.Failure("not used"));

        public Task<ToolchainResult> RemuxAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(ToolchainResult.Failure("not used"));
    }

    private sealed class ProbeWith : IVideoMetadataProbe
    {
        private readonly VideoProbeInfo _probe;

        public ProbeWith(VideoProbeInfo probe) => _probe = probe;

        public Task<VideoProbeInfo?> ProbeAsync(FilePath source, CancellationToken cancellationToken) => Task.FromResult<VideoProbeInfo?>(_probe);
    }

    private async Task<(VideoOptimiserOptions Options, VideoOptimiserPlan Plan, string Source)> PrepareAsync(string ffmpeg)
    {
        var source = Path.Combine(_directory, "static.mp4");

        // A flat screen that changes once, 25 fps for 6 s: decimation keeps only a handful of frames in any window.
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=c=0x336699:size=640x360:rate=25,drawbox=x=40:y=40:w=120:h=60:color=white:t=fill:enable='gte(t,3)'",
            "-t", "6", "-c:v", "libx264", "-preset", "veryfast", "-crf", "10", "-pix_fmt", "yuv420p", source);

        var options = new VideoOptimiserOptions
        {
            FfmpegPath = ffmpeg,
            FfprobePath = ToolAvailability.Ffprobe!,
            HardwareOverride = VideoHardwareCapabilities.CpuOnly,
            EnableFrameDecimation = true,
            QualitySearchMinimumDurationSeconds = 2,
            QualitySearchMaxSamples = 1,
            QualitySearchSampleSeconds = 1.5,
            QualitySearchFramesPerSample = 2,
            QualitySearchMaxProbes = 2
        };

        var video = new VideoStreamInfo("h264", "", "yuv420p", "bt709", "bt709", "bt709", "tv", 640, 360, 500_000, 25, false, false);
        var probe = new VideoProbeInfo("mov,mp4,m4a,3gp,3g2,mj2", "QuickTime / MOV", 6, 520_000, 400_000, video, null);
        var toolchain = new CapturingToolchain();
        var optimiser = new VideoOptimiser(options with { EnableQualitySearch = false }, toolchain, new ProbeWith(probe), NullVideoQualitySearch.Instance);
        var copy = FilePath.From(source);
        var request = new OptimisationRequest(ItemType.Video, copy);
        await optimiser.OptimiseAsync(request, new OptimiserExecutionContext(request.RequestId, _ => { }), CancellationToken.None);
        return (options, Assert.Single(toolchain.Plans), source);
    }

    [ToolFact("ffmpeg", "ffprobe")]
    public async Task DecimationRatioIsMeasuredFromFramesActuallyInTheWindow()
    {
        var (options, _, source) = await PrepareAsync(ToolAvailability.Ffmpeg!);
        var probe = new VideoProbeInfo(null, null, 6, null, null, new VideoStreamInfo("h264", "", "yuv420p", null, null, null, null, 640, 360, null, 25, false, false), null);

        var ratio = await new FfmpegVideoQualitySearch(options).MeasureDuplicateFrameRatioAsync(FilePath.From(source), probe, CancellationToken.None);

        // One change in six seconds of 25 fps: nearly every frame is a duplicate (mpdecimate still keeps one in 16 at most).
        Assert.NotNull(ratio);
        Assert.InRange(ratio!.Value, 0.85, 1.0);
    }

    [ToolFact("ffmpeg", "ffprobe")]
    public async Task ASecondSearchOnTheSameCacheReusesTheDecodedReferenceFrames()
    {
        var (options, plan, _) = await PrepareAsync(ToolAvailability.Ffmpeg!);
        var search = new FfmpegVideoQualitySearch(options);
        var logs = new List<string>();
        var previousSink = SharedLogger.Sink;
        SharedLogger.Sink = (_, message, _) =>
        {
            lock (logs)
            {
                logs.Add(message);
            }
        };

        try
        {
            using var cache = new VideoSearchCache();
            var context = new OptimiserExecutionContext("cache-test", _ => { });

            var first = await search.SearchAsync(plan, context, cache, CancellationToken.None);
            var second = await search.SearchAsync(plan, context, cache, CancellationToken.None);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.Crf, second!.Crf);
            lock (logs)
            {
                Assert.Single(logs, line => line.Contains("reference frames", StringComparison.Ordinal) && line.Contains("took", StringComparison.Ordinal));
                Assert.Single(logs, line => line.Contains("reference frames", StringComparison.Ordinal) && line.Contains("reused", StringComparison.Ordinal));
            }
        }
        finally
        {
            SharedLogger.Sink = previousSink;
        }
    }

    [ToolFact("ffmpeg", "ffprobe")]
    public async Task ASearchWithoutACacheStillWorksAndReleasesItsFrames()
    {
        var (options, plan, _) = await PrepareAsync(ToolAvailability.Ffmpeg!);

        var result = await new FfmpegVideoQualitySearch(options).SearchAsync(plan, new OptimiserExecutionContext("t", _ => { }), CancellationToken.None);

        Assert.NotNull(result);
    }
}
