using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Optimizers;
using Squish.Core.Settings;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>Regression tests for the video pipeline review (plan level, no ffmpeg needed).</summary>
[Collection("VideoTools")]
public sealed class VideoPipelineFixPlanTests
{
    public VideoPipelineFixPlanTests(VideoToolchainFixture tools) => _ = tools ?? throw new ArgumentNullException(nameof(tools));

    private static readonly VideoHardwareCapabilities NvencHevcOnly = new(
        SupportsNvenc: true, SupportsAmf: false, SupportsQsv: false, SupportsDxva: true,
        SupportsAv1Nvenc: false, SupportsAv1Amf: false, SupportsAv1Qsv: false,
        SupportsHevcNvenc: true, SupportsHevcAmf: false, SupportsHevcQsv: false);

    private static VideoProbeInfo Probe(int width = 1920, int height = 1080, string audio = "aac", long? audioBitrate = 128_000, bool interlaced = false, string videoCodec = "h264")
    {
        var video = new VideoStreamInfo(videoCodec, "", "yuv420p", "bt709", "bt709", "bt709", "tv", width, height, 8_000_000, 30, false, interlaced);
        var audioStream = string.IsNullOrEmpty(audio) ? null : new AudioStreamInfo(audio, "", 2, 48_000, audioBitrate);
        return new VideoProbeInfo("mov,mp4,m4a,3gp,3g2,mj2", "QuickTime / MOV", 10, 8_200_000, 10_000_000, video, audioStream);
    }

    private static async Task<VideoOptimiserPlan> PlanAsync(VideoProbeInfo probe, IReadOnlyDictionary<string, object?>? metadata = null, VideoHardwareCapabilities? hardware = null, string extension = ".mp4", VideoOptimiserOptions? options = null)
    {
        var source = FilePath.TempFile("plan-fix-test", extension, addUniqueSuffix: true);
        File.WriteAllBytes(source.Value, new byte[4096]);
        var toolchain = new CapturingToolchain();
        var optimiser = new VideoOptimiser(
            (options ?? VideoOptimiserOptions.Default) with { RequireSmallerSize = false, EnableQualitySearch = false, HardwareOverride = hardware ?? VideoHardwareCapabilities.CpuOnly },
            toolchain,
            new FixedProbe(probe));
        try
        {
            var request = new OptimisationRequest(ItemType.Video, source, metadata: metadata);
            var result = await optimiser.OptimiseAsync(request, new OptimiserExecutionContext(request.RequestId, _ => { }), CancellationToken.None);
            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            if (result.OutputPath is { } output && output.Value != source.Value && File.Exists(output.Value))
            {
                File.Delete(output.Value);
            }

            return Assert.Single(toolchain.Plans);
        }
        finally
        {
            File.Delete(source.Value);
        }
    }

    private static Dictionary<string, object?> Meta(params (string Key, object? Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value);

    // ---- encoder selection -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("GpuQuality")]
    [InlineData("GpuSimple")]
    [InlineData("GpuCqp")]
    public async Task GpuPresetsUseTheGpuEncoderWhenTheGpuHasNoAv1Encoder(string preset)
    {
        var plan = await PlanAsync(Probe(), Meta(("video.encoderPreset", preset)), NvencHevcOnly);

        Assert.True(plan.Encoder.UseHardwareEncoder);
        Assert.Equal("hevc_nvenc", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task AutoPresetStillPrefersSoftwareAv1()
    {
        var plan = await PlanAsync(Probe(), hardware: NvencHevcOnly);

        Assert.False(plan.Encoder.UseHardwareEncoder);
        Assert.Equal("libsvtav1", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task HardwareHevcInMp4IsTaggedHvc1()
    {
        var plan = await PlanAsync(Probe(), Meta(("video.encoderPreset", "GpuQuality")), NvencHevcOnly);

        var tagIndex = plan.VideoCodecArguments.ToList().IndexOf("-tag:v");
        Assert.True(tagIndex >= 0, "hevc in mp4 needs -tag:v hvc1 or QuickTime/Windows Explorer cannot play it");
        Assert.Equal("hvc1", plan.VideoCodecArguments[tagIndex + 1]);
    }

    [Fact]
    public async Task GpuConstantQualityPresetKeepsItsFixedQuantisersWhenNoQualitySearchRuns()
    {
        var amfHevc = new VideoHardwareCapabilities(
            SupportsNvenc: false, SupportsAmf: true, SupportsQsv: false, SupportsDxva: true,
            SupportsAv1Nvenc: false, SupportsAv1Amf: false, SupportsAv1Qsv: false,
            SupportsHevcNvenc: false, SupportsHevcAmf: true, SupportsHevcQsv: false);

        var cqp = (await PlanAsync(Probe(), Meta(("video.encoderPreset", "GpuCqp")), amfHevc)).VideoCodecArguments.ToList();
        Assert.Null((await PlanAsync(Probe(), Meta(("video.encoderPreset", "GpuCqp")), amfHevc)).Encoder.TargetBitrateKbps);
        Assert.DoesNotContain("-b:v", cqp);
        Assert.Equal("cqp", cqp[cqp.IndexOf("-rc") + 1]);
        Assert.Contains("-qp_i", cqp);

        // The other GPU presets stay bitrate-driven.
        var quality = (await PlanAsync(Probe(), Meta(("video.encoderPreset", "GpuQuality")), amfHevc)).VideoCodecArguments.ToList();
        Assert.Contains("-b:v", quality);
    }

    [Fact]
    public async Task SoftwareAv1UsesTheVisualQualityTune()
    {
        var plan = await PlanAsync(Probe());

        var args = plan.VideoCodecArguments.ToList();
        var index = args.IndexOf("-svtav1-params");
        Assert.True(index >= 0);
        Assert.Contains("tune=0", args[index + 1]);
        Assert.Contains("enable-variance-boost=1", args[index + 1]);
    }

    // ---- filters ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResolutionCapNeverUpscalesSmallerClips()
    {
        var plan = await PlanAsync(Probe(width: 400, height: 300), Meta(("video.maxWidth", 720), ("video.maxHeight", 720)));

        var scale = Assert.Single(plan.Filters, f => f.StartsWith("scale=", StringComparison.Ordinal));
        Assert.Contains("min(iw,720)", scale);
        Assert.Contains("min(ih,720)", scale);
        Assert.Contains("force_divisible_by=2", scale);
    }

    [Fact]
    public async Task OddSourceDimensionsAreMadeEncodableEvenWithoutAResizeCap()
    {
        var plan = await PlanAsync(Probe(width: 1365, height: 767));

        Assert.Contains("crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0", plan.Filters);
    }

    [Fact]
    public async Task EvenSourceDimensionsGetNoExtraFilter()
    {
        var plan = await PlanAsync(Probe(width: 1920, height: 1080));

        Assert.DoesNotContain(plan.Filters, f => f.StartsWith("crop=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InterlacedSourcesAreDeinterlacedBeforeAnythingElse()
    {
        var plan = await PlanAsync(Probe(interlaced: true));

        Assert.StartsWith("bwdif=", plan.Filters[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressiveSourcesAreNotDeinterlaced()
    {
        var plan = await PlanAsync(Probe(interlaced: false));

        Assert.DoesNotContain(plan.Filters, f => f.StartsWith("bwdif", StringComparison.Ordinal));
    }

    // ---- audio -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlaybackSpeedRetimesTheAudioInsteadOfCopyingIt()
    {
        var plan = await PlanAsync(Probe(), Meta(("video.playbackSpeed", 2.0)));

        Assert.Contains(plan.Filters, f => f.StartsWith("setpts=PTS/", StringComparison.Ordinal));
        Assert.False(plan.Audio.CopyStream);
        Assert.Equal(2.0, plan.Audio.TempoFactor);

        var args = new ExternalVideoToolchain(VideoOptimiserOptions.Default).BuildTranscodeArguments(plan, FilePath.From(Path.Combine(Path.GetTempPath(), "out.mp4")), includeAudio: true);
        var af = args.IndexOf("-af");
        Assert.True(af >= 0);
        Assert.Equal("atempo=2", args[af + 1]);
    }

    [Fact]
    public async Task PlaybackSpeedOverridesAnExplicitAudioCopy()
    {
        var plan = await PlanAsync(Probe(), Meta(("video.playbackSpeed", 1.5), ("video.audioCodec", "copy")));

        Assert.False(plan.Audio.CopyStream);
        Assert.NotNull(plan.Audio.Encoder);
    }

    [Fact]
    public async Task NormalSpeedKeepsAudioCopy()
    {
        var plan = await PlanAsync(Probe(), Meta(("video.playbackSpeed", 1.0)));

        Assert.True(plan.Audio.CopyStream);
        Assert.Null(plan.Audio.TempoFactor);
    }

    [Theory]
    [InlineData(2.0, new[] { "atempo=2" })]
    [InlineData(4.0, new[] { "atempo=2.0", "atempo=2" })]
    [InlineData(0.25, new[] { "atempo=0.5", "atempo=0.5" })]
    [InlineData(1.5, new[] { "atempo=1.5" })]
    public void TempoIsChainedWithinAtempoLimits(double factor, string[] expected)
    {
        Assert.Equal(expected, VideoOptimiser.BuildTempoFilters(factor));
    }

    [Theory]
    [InlineData("flac", false)]
    [InlineData("pcm_s24le", false)]
    [InlineData("truehd", false)]
    [InlineData("dts", false)]
    [InlineData("aac", true)]
    [InlineData("opus", true)]
    public async Task MatroskaAudioIsOnlyCopiedWhenAlreadyCompressed(string audioCodec, bool copied)
    {
        // Matroska headers usually carry no bitrate; that must not make lossless audio look "cheap enough to copy".
        var options = VideoOptimiserOptions.Default with { ForceMp4 = false };
        var plan = await PlanAsync(Probe(audio: audioCodec, audioBitrate: null), extension: ".mkv", options: options);

        Assert.Equal("mkv", plan.OutputExtension);
        Assert.Equal(copied, plan.Audio.CopyStream);
    }

    // ---- GPU decode + CPU filters ----------------------------------------------------------------------------

    [Fact]
    public async Task GpuFramesAreNotKeptOnTheGpuWhenCpuFiltersRun()
    {
        var toolchain = new ExternalVideoToolchain(VideoOptimiserOptions.Default);
        var output = FilePath.From(Path.Combine(Path.GetTempPath(), "out.mp4"));

        // Frame decimation (on by default) is a CPU filter.
        var filtered = await PlanAsync(Probe(), Meta(("video.encoderPreset", "GpuQuality")), NvencHevcOnly);
        Assert.True(filtered.RequiresFilters);
        var filteredArgs = toolchain.BuildTranscodeArguments(filtered, output, includeAudio: true);
        Assert.Contains("-hwaccel", filteredArgs);
        Assert.DoesNotContain("-hwaccel_output_format", filteredArgs);
        Assert.Contains("hevc_nvenc", filteredArgs);

        // Nothing to filter: keep the fully-on-GPU path.
        var unfiltered = await PlanAsync(Probe(), Meta(("video.encoderPreset", "GpuQuality"), ("video.frameDecimation", false), ("video.capFps", false)), NvencHevcOnly);
        Assert.False(unfiltered.RequiresFilters);
        Assert.Contains("-hwaccel_output_format", toolchain.BuildTranscodeArguments(unfiltered, output, includeAudio: true));
    }

    // ---- settings --------------------------------------------------------------------------------------------

    [Fact]
    public void VideoOptionsFollowTheSavedSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "squish-vopt-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SettingsStore(directory);
            var defaults = VideoOptimiserOptionsFactory.Apply(VideoOptimiserOptions.Default, store);
            Assert.True(defaults.StripMetadata, "'Strip camera & location metadata' is on by default and must reach videos");
            Assert.True(defaults.PreserveTimestamps);
            Assert.True(defaults.PreserveColorMetadata);

            store.Set(SettingsRegistry.StripMetadata, false);
            store.Set(SettingsRegistry.PreserveDates, false);
            store.Set(SettingsRegistry.PreserveColorMetadata, false);
            store.Set(SettingsRegistry.RemoveAudioFromVideos, true);

            var changed = VideoOptimiserOptionsFactory.Apply(VideoOptimiserOptions.Default, store);
            Assert.False(changed.StripMetadata);
            Assert.False(changed.PreserveTimestamps);
            Assert.False(changed.PreserveColorMetadata);
            Assert.True(changed.RemoveAudio);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task StripMetadataSettingBecomesMapMetadataArguments()
    {
        var plan = await PlanAsync(Probe(), options: VideoOptimiserOptions.Default with { StripMetadata = true });
        Assert.True(plan.StripMetadata);

        var args = new ExternalVideoToolchain(VideoOptimiserOptions.Default).BuildTranscodeArguments(plan, FilePath.From(Path.Combine(Path.GetTempPath(), "out.mp4")), includeAudio: true);
        var index = args.IndexOf("-map_metadata");
        Assert.True(index >= 0);
        Assert.Equal("-1", args[index + 1]);
    }

    // ---- timestamps ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReplacingTheOriginalInPlaceKeepsItsTimestamps()
    {
        var source = FilePath.TempFile("video-times", ".mp4", addUniqueSuffix: true);
        File.WriteAllBytes(source.Value, new byte[4096]);
        var original = new DateTime(2020, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source.Value, original);
        File.SetCreationTimeUtc(source.Value, original.AddDays(-3));

        try
        {
            var optimiser = new VideoOptimiser(
                VideoOptimiserOptions.Default with { EnableQualitySearch = false, HardwareOverride = VideoHardwareCapabilities.CpuOnly },
                new CapturingToolchain(),
                new FixedProbe(Probe()));
            var metadata = new Dictionary<string, object?> { [OptimisationMetadata.OutputReplaceOriginal] = true };
            var request = new OptimisationRequest(ItemType.Video, source, metadata: metadata);

            var result = await optimiser.OptimiseAsync(request, new OptimiserExecutionContext(request.RequestId, _ => { }), CancellationToken.None);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Equal(source, result.OutputPath);
            Assert.Equal(512, new FileInfo(source.Value).Length); // really replaced
            Assert.Equal(original, File.GetLastWriteTimeUtc(source.Value));
            Assert.Equal(original.AddDays(-3), File.GetCreationTimeUtc(source.Value));
        }
        finally
        {
            File.Delete(source.Value);
        }
    }

    private sealed class CapturingToolchain : IVideoToolchain
    {
        public List<VideoOptimiserPlan> Plans { get; } = new();

        public Task<ToolchainResult> TranscodeAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            Plans.Add(plan);
            File.WriteAllBytes(tempOutput.Value, new byte[512]);
            return Task.FromResult(ToolchainResult.Successful());
        }

        public Task<ToolchainResult> ConvertToAnimatedAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(ToolchainResult.Failure("not used"));

        public Task<ToolchainResult> RemuxAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            Plans.Add(plan);
            File.WriteAllBytes(tempOutput.Value, new byte[256]);
            return Task.FromResult(ToolchainResult.Successful());
        }
    }

    private sealed class FixedProbe : IVideoMetadataProbe
    {
        private readonly VideoProbeInfo _probe;

        public FixedProbe(VideoProbeInfo probe) => _probe = probe;

        public Task<VideoProbeInfo?> ProbeAsync(FilePath source, CancellationToken cancellationToken) => Task.FromResult<VideoProbeInfo?>(_probe);
    }
}

/// <summary>End-to-end checks against a real ffmpeg (skipped when none is installed).</summary>
public sealed class VideoPipelineFixRealFfmpegTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "squish-video-fix-" + Guid.NewGuid().ToString("N"));

    public VideoPipelineFixRealFfmpegTests() => Directory.CreateDirectory(_directory);

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

    [ToolFact("ffmpeg", "ffprobe")]
    public async Task QualitySearchStillWorksOnAStaticScreenRecordingThatFrameDecimationGuts()
    {
        var ffmpeg = ToolAvailability.Ffmpeg!;
        var source = Path.Combine(_directory, "static.mp4");

        // A flat screen that changes once: mpdecimate leaves only a handful of frames in any sampled window, far fewer than
        // the seconds x fps the sampler used to count on.
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
            QualitySearchFramesPerSample = 2
        };

        var plan = await BuildPlanAsync(options, source);
        Assert.Contains(plan.Filters, f => f.StartsWith("mpdecimate", StringComparison.Ordinal));

        var result = await new FfmpegVideoQualitySearch(options).SearchAsync(plan, new OptimiserExecutionContext("t", _ => { }), CancellationToken.None);

        Assert.NotNull(result);
    }

    [ToolFact("ffmpeg", "ffprobe")]
    public async Task GeneratedFiltersEncodeOddSizedAndSmallClipsCorrectly()
    {
        var ffmpeg = ToolAvailability.Ffmpeg!;
        var ffprobe = ToolAvailability.Ffprobe!;
        var odd = Path.Combine(_directory, "odd.mp4");
        var small = Path.Combine(_directory, "small.mp4");
        // Odd sides cannot be written as yuv420p, so the odd clip is made with a 4:4:4 encode that accepts them.
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1365x767:rate=25", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv444p", odd);
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=400x300:rate=25", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", small);

        var options = new VideoOptimiserOptions { FfmpegPath = ffmpeg, FfprobePath = ffprobe, HardwareOverride = VideoHardwareCapabilities.CpuOnly };

        var oddPlan = await BuildPlanAsync(options, odd);
        var oddOut = Path.Combine(_directory, "odd-out.mp4");
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", odd, "-vf", string.Join(',', oddPlan.Filters), "-c:v", "libx264", "-pix_fmt", "yuv420p", oddOut);
        Assert.Equal("1364x766", (await Run(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", oddOut)).Trim());

        var capped = await BuildPlanAsync(options, small, new Dictionary<string, object?> { ["video.maxWidth"] = 720, ["video.maxHeight"] = 720 });
        var smallOut = Path.Combine(_directory, "small-out.mp4");
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", small, "-vf", string.Join(',', capped.Filters), "-c:v", "libx264", "-pix_fmt", "yuv420p", smallOut);
        Assert.Equal("400x300", (await Run(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", smallOut)).Trim());
    }

    private static async Task<VideoOptimiserPlan> BuildPlanAsync(VideoOptimiserOptions options, string path, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var probe = await new FfprobeMetadataProbe(options).ProbeAsync(FilePath.From(path), CancellationToken.None);
        Assert.NotNull(probe);
        var capture = new PlanCapture();
        var optimiser = new VideoOptimiser(options with { EnableQualitySearch = false }, capture, new StaticProbe(probe!), NullVideoQualitySearch.Instance);
        var request = new OptimisationRequest(ItemType.Video, FilePath.From(path), metadata: metadata);
        await optimiser.OptimiseAsync(request, new OptimiserExecutionContext(request.RequestId, _ => { }), CancellationToken.None);
        return capture.Plan!;
    }

    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var result = await Squish.Core.Processes.ProcessRunner.RunAsync(executable, arguments);
        return result.StandardOutput;
    }

    private sealed class PlanCapture : IVideoToolchain
    {
        public VideoOptimiserPlan? Plan { get; private set; }

        public Task<ToolchainResult> TranscodeAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            Plan = plan;
            File.WriteAllBytes(tempOutput.Value, new byte[64]);
            return Task.FromResult(ToolchainResult.Successful());
        }

        public Task<ToolchainResult> ConvertToAnimatedAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(ToolchainResult.Failure("n/a"));

        public Task<ToolchainResult> RemuxAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            Plan = plan;
            File.WriteAllBytes(tempOutput.Value, new byte[64]);
            return Task.FromResult(ToolchainResult.Successful());
        }
    }

    private sealed class StaticProbe : IVideoMetadataProbe
    {
        private readonly VideoProbeInfo _probe;

        public StaticProbe(VideoProbeInfo probe) => _probe = probe;

        public Task<VideoProbeInfo?> ProbeAsync(FilePath source, CancellationToken cancellationToken) => Task.FromResult<VideoProbeInfo?>(_probe);
    }
}
