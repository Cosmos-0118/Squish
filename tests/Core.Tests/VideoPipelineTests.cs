using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>Plan-level behaviour of the video optimiser (no ffmpeg needed).</summary>
[Collection("VideoTools")]
public sealed class VideoPlanTests
{
    public VideoPlanTests(VideoToolchainFixture tools) => _ = tools ?? throw new ArgumentNullException(nameof(tools));

    private static VideoProbeInfo Probe(
        string codec = "h264",
        string pixelFormat = "yuv420p",
        double fps = 30,
        string audio = "aac",
        long audioBitrate = 128_000,
        int audioChannels = 2,
        bool hdr = false,
        double duration = 10)
    {
        var video = new VideoStreamInfo(codec, "", pixelFormat, "bt709", hdr ? "smpte2084" : "bt709", "bt709", "tv", 1920, 1080, 8_000_000, fps, hdr, false);
        var audioStream = string.IsNullOrEmpty(audio) ? null : new AudioStreamInfo(audio, "", audioChannels, 48_000, audioBitrate);
        return new VideoProbeInfo("mov,mp4,m4a,3gp,3g2,mj2", "QuickTime / MOV", duration, 8_200_000, 10_000_000, video, audioStream);
    }

    private static async Task<VideoOptimiserPlan> PlanAsync(VideoProbeInfo probe, VideoOptimiserOptions? options = null, IReadOnlyDictionary<string, object?>? metadata = null, string extension = ".mp4")
    {
        var source = FilePath.TempFile("plan-test", extension, addUniqueSuffix: true);
        File.WriteAllBytes(source.Value, new byte[4096]);
        var toolchain = new CapturingToolchain();
        var optimiser = new VideoOptimiser(
            (options ?? VideoOptimiserOptions.Default) with { RequireSmallerSize = false, EnableQualitySearch = false, HardwareOverride = VideoHardwareCapabilities.CpuOnly },
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

    [Fact]
    public async Task FrameRateCapNeverDuplicatesFramesOfSlowerSources()
    {
        var plan = await PlanAsync(Probe(fps: 30));
        Assert.DoesNotContain(plan.Filters, filter => filter.StartsWith("fps=", StringComparison.Ordinal));
        Assert.False(plan.CapFps);
    }

    [Fact]
    public async Task FrameRateCapAppliesToFasterSources()
    {
        var plan = await PlanAsync(Probe(fps: 120));
        Assert.Contains("fps=60", plan.Filters);
    }

    [Fact]
    public async Task FrameRateCapIsSkippedWhenTheSourceRateIsUnknown()
    {
        var plan = await PlanAsync(Probe(fps: 0));
        Assert.DoesNotContain(plan.Filters, filter => filter.StartsWith("fps=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HevcInMp4IsTaggedHvc1AndStays8BitFor8BitSources()
    {
        var plan = await PlanAsync(Probe());

        Assert.Equal(VideoCodec.Hevc, plan.Encoder.Codec);
        Assert.Equal("yuv420p", plan.Encoder.PixelFormat);
        var args = plan.VideoCodecArguments.ToList();
        Assert.Equal("hvc1", args[args.IndexOf("-tag:v") + 1]);
    }

    [Fact]
    public async Task TenBitAndHdrSourcesKeepTenBits()
    {
        var tenBit = await PlanAsync(Probe(codec: "hevc", pixelFormat: "yuv420p10le"));
        var hdr = await PlanAsync(Probe(codec: "hevc", pixelFormat: "yuv420p10le", hdr: true));

        Assert.Equal("yuv420p10le", tenBit.Encoder.PixelFormat);
        Assert.Equal("yuv420p10le", hdr.Encoder.PixelFormat);
    }

    [Fact]
    public async Task Vp9UsesLibvpxSpeedFlagsAndTwoPassOnlyThere()
    {
        var vp9 = await PlanAsync(Probe(codec: "vp9", audio: "opus", duration: 120), extension: ".webm");
        var args = vp9.VideoCodecArguments.ToList();

        Assert.Equal(VideoCodec.Vp9, vp9.Encoder.Codec);
        Assert.Contains("-cpu-used", args);
        Assert.Contains("-deadline", args);
        Assert.DoesNotContain("-preset", args);
        Assert.Equal("0", args[args.IndexOf("-b:v") + 1]);
        Assert.True(vp9.UseTwoPass);

        var hevc = await PlanAsync(Probe(duration: 120));
        Assert.False(hevc.UseTwoPass);
    }

    [Fact]
    public async Task X265AndSvtAv1DoNotReceiveX264OnlyFlags()
    {
        var hevc = await PlanAsync(Probe());
        var av1 = await PlanAsync(Probe(), metadata: new Dictionary<string, object?> { ["video.codec"] = "av1" });

        Assert.DoesNotContain("-rc-lookahead", hevc.VideoCodecArguments);
        Assert.DoesNotContain("-sc_threshold", hevc.VideoCodecArguments);
        Assert.DoesNotContain("-sc_threshold", av1.VideoCodecArguments);
        Assert.Equal(VideoCodec.Av1, av1.Encoder.Codec);
    }

    [Fact]
    public async Task X264KeepsLookaheadTuning()
    {
        var plan = await PlanAsync(Probe(), metadata: new Dictionary<string, object?> { ["video.codec"] = "h264" });

        Assert.Equal(VideoCodec.H264, plan.Encoder.Codec);
        Assert.Contains("-rc-lookahead", plan.VideoCodecArguments);
    }

    [Fact]
    public async Task CompatibleAudioIsCopiedByDefault()
    {
        var plan = await PlanAsync(Probe(audio: "aac", audioBitrate: 160_000));
        Assert.True(plan.Audio.CopyStream);
        Assert.False(plan.Audio.NormalizeLoudness);
    }

    [Fact]
    public async Task ExcessiveAudioBitratesAreReEncoded()
    {
        var plan = await PlanAsync(Probe(audio: "aac", audioBitrate: 640_000, audioChannels: 6));
        Assert.False(plan.Audio.CopyStream);
        Assert.Equal(2, plan.Audio.Channels);
    }

    [Fact]
    public async Task IncompatibleAudioIsReEncoded()
    {
        var plan = await PlanAsync(Probe(audio: "pcm_s16le", audioBitrate: 768_000));
        Assert.False(plan.Audio.CopyStream);
        Assert.Equal("aac", plan.Audio.Encoder);
    }

    [Fact]
    public void LoudnessNormalisationIsOptIn()
        => Assert.False(VideoOptimiserOptions.Default.EnableAudioNormalization);

    [Fact]
    public void FfprobeNumbersParseIdenticallyInEveryCulture()
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":1920,"height":1080,"avg_frame_rate":"30000/1001","r_frame_rate":"30000/1001","bit_rate":"8000000"}],
             "format":{"format_name":"mov,mp4","duration":"10.500000","bit_rate":"8200000","size":"10762500"}}
            """;
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var probe = VideoProbeParser.TryParse(json);

            Assert.NotNull(probe);
            Assert.Equal(10.5, probe!.DurationSeconds!.Value, 3);
            Assert.Equal(29.97, probe.Video!.FrameRate!.Value, 2);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void HlgCountsAsHdr()
    {
        const string json = """{"streams":[{"codec_type":"video","codec_name":"hevc","pix_fmt":"yuv420p10le","color_transfer":"arib-std-b67"}],"format":{}}""";
        Assert.True(VideoProbeParser.TryParse(json)!.Video!.IsHdr);
    }

    [Fact]
    public void MissingFfmpegDoesNotCrashOptionInitialisation()
    {
        using var ffmpeg = new EnvironmentVariableScope("CLOP_FFMPEG", null);
        using var legacy = new EnvironmentVariableScope("FFMPEG_EXECUTABLE", null);
        using var emptyPath = new EnvironmentVariableScope("PATH", string.Empty);

        var options = new VideoOptimiserOptions();

        Assert.False(string.IsNullOrWhiteSpace(options.FfmpegPath));
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

public sealed class CrfSearchTests
{
    // Roughly the measured behaviour of x265: ~2.6 score points per CRF step, bitrate -11% per step.
    private static Func<int, CancellationToken, Task<CrfProbe?>> Model(double scoreAtCrf20, double perStep, int[] calls)
        => (crf, _) =>
        {
            calls[0]++;
            return Task.FromResult<CrfProbe?>(new CrfProbe(crf, scoreAtCrf20 - perStep * (crf - 20), 4000 * Math.Exp(-0.11 * (crf - 20))));
        };

    [Fact]
    public async Task FindsTheHighestCrfThatMeetsTheTarget()
    {
        var calls = new int[1];
        var outcome = await CrfSearch.FindAsync(26, 18, 36, 74, -2.6, Model(90, 2.6, calls), 6, CancellationToken.None, tolerance: 0.1);

        Assert.NotNull(outcome);
        Assert.True(outcome!.TargetMet);
        // score(crf) = 90 - 2.6*(crf-20) >= 74  =>  crf <= 26.15
        Assert.Equal(26, outcome.Chosen.Crf);
        Assert.True(outcome.Chosen.Score >= 74);
    }

    [Fact]
    public async Task ConvergesInFewProbesWithTheSlopePrior()
    {
        var calls = new int[1];
        var outcome = await CrfSearch.FindAsync(24, 18, 36, 74, -2.6, Model(95, 2.6, calls), 6, CancellationToken.None);

        Assert.True(outcome!.TargetMet);
        Assert.InRange(calls[0], 1, 4);
    }

    [Fact]
    public async Task FallsBackToTheLowestCrfWhenTheTargetIsUnreachable()
    {
        var calls = new int[1];
        var outcome = await CrfSearch.FindAsync(26, 18, 36, 90, -2.6, Model(70, 2.6, calls), 6, CancellationToken.None);

        Assert.False(outcome!.TargetMet);
        Assert.Equal(18, outcome.Chosen.Crf);
    }

    [Fact]
    public async Task ClimbsToTheMaximumWhenEverythingPasses()
    {
        var calls = new int[1];
        var outcome = await CrfSearch.FindAsync(26, 18, 30, 40, -2.6, Model(99, 0.5, calls), 8, CancellationToken.None);

        Assert.True(outcome!.TargetMet);
        Assert.Equal(30, outcome.Chosen.Crf);
    }

    [Fact]
    public async Task StopsWhenTheProbeFails()
    {
        var outcome = await CrfSearch.FindAsync(26, 18, 36, 74, -2.6, (_, _) => Task.FromResult<CrfProbe?>(null), 6, CancellationToken.None);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task BitrateCapRaisesTheCrfOnGrainyFootage()
    {
        var calls = new int[1];
        var model = Model(60, 2.6, calls); // never reaches 90 -> would sit at the lowest CRF
        var first = await CrfSearch.FindAsync(26, 18, 36, 90, -2.6, model, 4, CancellationToken.None);
        Assert.False(first!.TargetMet);
        var cost = first.Chosen.BitrateKbps;

        var capped = await CrfSearch.EnforceBitrateCapAsync(first, cost * 0.5, 36, model, CancellationToken.None);

        Assert.True(capped.Chosen.Crf > first.Chosen.Crf);
        Assert.True(capped.Chosen.BitrateKbps <= cost * 0.5 * 1.15);
        Assert.False(capped.TargetMet);
    }

    [Fact]
    public async Task BitrateCapLeavesAffordableEncodesAlone()
    {
        var calls = new int[1];
        var model = Model(90, 2.6, calls);
        var first = await CrfSearch.FindAsync(26, 18, 36, 74, -2.6, model, 6, CancellationToken.None);
        var before = calls[0];

        var capped = await CrfSearch.EnforceBitrateCapAsync(first!, first!.Chosen.BitrateKbps * 2, 36, model, CancellationToken.None);

        Assert.Equal(first.Chosen, capped.Chosen);
        Assert.Equal(before, calls[0]);
    }
}

/// <summary>End-to-end runs against a real ffmpeg (skipped when none is installed).</summary>
public sealed class RealFfmpegVideoTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clop-video-e2e-" + Guid.NewGuid().ToString("N"));

    public RealFfmpegVideoTests() => Directory.CreateDirectory(_directory);

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
    public async Task OptimisesARealClipKeepingFrameRateTagsAndFaststart()
    {
        var ffmpeg = ToolAvailability.Ffmpeg!;
        var source = Path.Combine(_directory, "source.mp4");
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "3", "-c:v", "libx264", "-preset", "veryfast", "-crf", "12", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", source);

        using var ffmpegEnv = new EnvironmentVariableScope("CLOP_FFMPEG", ffmpeg);
        using var ffprobeEnv = new EnvironmentVariableScope("CLOP_FFPROBE", ToolAvailability.Ffprobe);
        var options = new VideoOptimiserOptions
        {
            FfmpegPath = ffmpeg,
            FfprobePath = ToolAvailability.Ffprobe!,
            HardwareOverride = VideoHardwareCapabilities.CpuOnly,
            EnableQualitySearch = false,
            RequireSmallerSize = true
        };

        var optimiser = new VideoOptimiser(options);
        var request = new OptimisationRequest(ItemType.Video, FilePath.From(source));
        var result = await optimiser.OptimiseAsync(request, new OptimiserExecutionContext(request.RequestId, _ => { }), CancellationToken.None);

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        var output = result.OutputPath!.Value.Value;
        Assert.NotEqual(source, output);
        Assert.True(new FileInfo(output).Length < new FileInfo(source).Length);

        var info = (await Run(ToolAvailability.Ffprobe!, "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,codec_tag_string,pix_fmt,avg_frame_rate,nb_read_frames", "-of", "default=nw=1", output)).Replace("\r", string.Empty);
        Assert.Contains("codec_name=hevc", info);
        Assert.Contains("codec_tag_string=hvc1", info);
        Assert.Contains("pix_fmt=yuv420p\n", info + "\n");
        Assert.Contains("avg_frame_rate=30/1", info);
        Assert.Contains("nb_read_frames=90", info);

        var topLevel = ReadTopLevelAtoms(output);
        Assert.True(topLevel.IndexOf("moov") < topLevel.IndexOf("mdat"), "moov must precede mdat (faststart)");
    }

    [ToolFact("ffmpeg", "ffprobe")]
    public async Task QualitySearchPicksAHigherCrfForCleanFootageThanForNoise()
    {
        var ffmpeg = ToolAvailability.Ffmpeg!;
        var clean = Path.Combine(_directory, "clean.mp4");
        var noisy = Path.Combine(_directory, "noisy.mp4");
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "gradients=size=640x360:rate=25:speed=0.02", "-t", "6", "-c:v", "libx264", "-preset", "veryfast", "-crf", "10", "-pix_fmt", "yuv420p", clean);
        await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "mandelbrot=size=640x360:rate=25,noise=alls=25:allf=t+u", "-t", "6", "-c:v", "libx264", "-preset", "veryfast", "-crf", "10", "-pix_fmt", "yuv420p", noisy);

        var options = new VideoOptimiserOptions
        {
            FfmpegPath = ffmpeg,
            FfprobePath = ToolAvailability.Ffprobe!,
            HardwareOverride = VideoHardwareCapabilities.CpuOnly,
            QualitySearchMinimumDurationSeconds = 2,
            QualitySearchMaxSamples = 1,
            QualitySearchSampleSeconds = 1.5,
            QualitySearchFramesPerSample = 2
        };

        var search = new FfmpegVideoQualitySearch(options);
        var cleanPlan = await BuildPlanAsync(options, clean);
        var noisyPlan = await BuildPlanAsync(options, noisy);

        var cleanResult = await search.SearchAsync(cleanPlan, new OptimiserExecutionContext("t", _ => { }), CancellationToken.None);
        var noisyResult = await search.SearchAsync(noisyPlan, new OptimiserExecutionContext("t", _ => { }), CancellationToken.None);

        Assert.NotNull(cleanResult);
        Assert.NotNull(noisyResult);
        Assert.True(cleanResult!.Crf >= noisyResult!.Crf, $"clean footage ({cleanResult.Crf}) should tolerate a CRF at least as high as noise ({noisyResult.Crf})");
    }

    private static async Task<VideoOptimiserPlan> BuildPlanAsync(VideoOptimiserOptions options, string path)
    {
        var probe = await new FfprobeMetadataProbe(options).ProbeAsync(FilePath.From(path), CancellationToken.None);
        Assert.NotNull(probe);
        var capture = new PlanCapture();
        var optimiser = new VideoOptimiser(options with { EnableQualitySearch = false }, capture, new StaticProbe(probe!), NullVideoQualitySearch.Instance);
        var request = new OptimisationRequest(ItemType.Video, FilePath.From(path));
        await optimiser.OptimiseAsync(request, new OptimiserExecutionContext(request.RequestId, _ => { }), CancellationToken.None);
        return capture.Plan!;
    }

    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var result = await ClopWindows.Core.Processes.ProcessRunner.RunAsync(executable, arguments);
        return result.StandardOutput;
    }

    private static List<string> ReadTopLevelAtoms(string path)
    {
        var atoms = new List<string>();
        using var stream = File.OpenRead(path);
        var header = new byte[16];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(header, 0, 8);
            long size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            atoms.Add(System.Text.Encoding.ASCII.GetString(header, 4, 4));
            if (size == 1)
            {
                stream.ReadExactly(header, 8, 8);
                size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                stream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (size == 0)
            {
                break;
            }
            else
            {
                stream.Seek(size - 8, SeekOrigin.Current);
            }
        }

        return atoms;
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
