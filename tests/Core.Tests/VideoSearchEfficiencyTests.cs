using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>Content-aware Auto selection, faster sampling presets for GPU encoders, and the cached SSIMULACRA2 reference.</summary>
[Collection("VideoTools")]
public sealed class VideoSearchEfficiencyTests
{
    public VideoSearchEfficiencyTests(VideoToolchainFixture tools) => _ = tools ?? throw new ArgumentNullException(nameof(tools));

    // ---- sampling presets ------------------------------------------------------------------------------------

    private static string[] Faster(VideoCodec codec, params string[] args)
        => FfmpegVideoQualitySearch.FasterPresetForSampling(args, codec).ToArray();

    [Fact]
    public void NvencSamplesSkipTheSlowestPresetMultipassAndUhqTune()
    {
        var args = Faster(VideoCodec.Hevc, "-c:v", "hevc_nvenc", "-preset", "p7", "-tune", "uhq", "-rc", "vbr", "-multipass", "qres", "-spatial-aq", "1");

        Assert.Equal(new[] { "-c:v", "hevc_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-multipass", "disabled", "-spatial-aq", "1" }, args);
    }

    [Fact]
    public void NvencAv1SamplesAreSpedUpToo()
    {
        // "p7" is not a number, so the SVT-AV1 rule used to leave it alone.
        var args = Faster(VideoCodec.Av1, "-preset", "p7");

        Assert.Equal(new[] { "-preset", "p4" }, args);
    }

    [Fact]
    public void AnNvencPresetAlreadyAtOrBelowMediumIsKept()
        => Assert.Equal(new[] { "-preset", "p3" }, Faster(VideoCodec.Hevc, "-preset", "p3"));

    [Fact]
    public void AmfSamplesUseTheBalancedPreset()
    {
        var args = Faster(VideoCodec.Hevc, "-quality", "quality", "-usage", "transcoding");

        Assert.Equal(new[] { "-quality", "balanced", "-usage", "transcoding" }, args);
    }

    [Theory]
    [InlineData(VideoCodec.Hevc)]
    [InlineData(VideoCodec.Av1)]
    [InlineData(VideoCodec.H264)]
    public void QsvNamedPresetsAreSpedUpForEveryCodec(VideoCodec codec)
        => Assert.Equal(new[] { "-preset", "fast" }, Faster(codec, "-preset", "slower"));

    [Fact]
    public void SoftwareSamplingIsUnchanged()
    {
        Assert.Equal(new[] { "-preset", "8" }, Faster(VideoCodec.Av1, "-preset", "6"));
        Assert.Equal(new[] { "-preset", "fast" }, Faster(VideoCodec.Hevc, "-preset", "medium"));
        Assert.Equal(new[] { "-deadline", "good", "-cpu-used", "4" }, Faster(VideoCodec.Vp9, "-deadline", "good", "-cpu-used", "2"));
        Assert.Equal(new[] { "-preset", "ultrafast" }, Faster(VideoCodec.H264, "-preset", "ultrafast"));
    }

    // ---- content-aware Auto ----------------------------------------------------------------------------------

    private static readonly VideoHardwareCapabilities Rdna4 = new(false, true, false, true, false, true, false, false, true, false);

    private static VideoProbeInfo Probe(double duration, int width = 1920, int height = 910, double fps = 30)
    {
        var video = new VideoStreamInfo("h264", "", "yuv420p", "bt709", "bt709", "bt709", "tv", width, height, 8_000_000, fps, false, false);
        var audio = new AudioStreamInfo("aac", "", 2, 48_000, 128_000);
        return new VideoProbeInfo("mov,mp4,m4a,3gp,3g2,mj2", "QuickTime / MOV", duration, 8_200_000, 10_000_000, video, audio);
    }

    private sealed class DecimationSearch : IVideoQualitySearch
    {
        private readonly double? _ratio;

        public DecimationSearch(double? ratio) => _ratio = ratio;

        public int Measurements { get; private set; }

        public Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult<VideoQualitySearchResult?>(null);

        public Task<double?> MeasureDuplicateFrameRatioAsync(FilePath source, VideoProbeInfo probe, CancellationToken cancellationToken)
        {
            Measurements++;
            return Task.FromResult(_ratio);
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
            => Task.FromResult(ToolchainResult.Failure("not used"));
    }

    private sealed class FixedProbe : IVideoMetadataProbe
    {
        private readonly VideoProbeInfo _probe;

        public FixedProbe(VideoProbeInfo probe) => _probe = probe;

        public Task<VideoProbeInfo?> ProbeAsync(FilePath source, CancellationToken cancellationToken) => Task.FromResult<VideoProbeInfo?>(_probe);
    }

    private static async Task<(VideoOptimiserPlan Plan, int Measurements)> PlanAsync(VideoProbeInfo probe, double? duplicateRatio, IReadOnlyDictionary<string, object?>? metadata = null, VideoOptimiserOptions? options = null)
    {
        var source = FilePath.TempFile("content-aware-test", ".mp4", addUniqueSuffix: true);
        File.WriteAllBytes(source.Value, new byte[4096]);
        var toolchain = new CapturingToolchain();
        var search = new DecimationSearch(duplicateRatio);
        var optimiser = new VideoOptimiser(
            (options ?? VideoOptimiserOptions.Default) with { RequireSmallerSize = false, HardwareOverride = Rdna4, PowerStateOverride = VideoPowerState.Ac },
            toolchain,
            new FixedProbe(probe),
            search);
        try
        {
            var request = new OptimisationRequest(ItemType.Video, source, metadata: metadata);
            var result = await optimiser.OptimiseAsync(request, new OptimiserExecutionContext(request.RequestId, _ => { }), CancellationToken.None);
            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            if (result.OutputPath is { } output && output.Value != source.Value && File.Exists(output.Value))
            {
                File.Delete(output.Value);
            }

            return (Assert.Single(toolchain.Plans), search.Measurements);
        }
        finally
        {
            File.Delete(source.Value);
        }
    }

    // 102 s of 1920x910 at 30 fps is 5,364 MP raw: over the first-generation AV1 threshold (3,700 MP).

    [Fact]
    public async Task AStaticScreenRecordingThatDecimationShrinksStaysInSoftware()
    {
        var (plan, measurements) = await PlanAsync(Probe(102), duplicateRatio: 0.37);

        Assert.Equal(1, measurements);
        Assert.Equal("libsvtav1", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task ContinuousMotionFootageOfTheSameSizeGoesToTheGpu()
    {
        var (plan, measurements) = await PlanAsync(Probe(102), duplicateRatio: 0.0);

        Assert.Equal(1, measurements);
        Assert.Equal("av1_amf", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task WhenDecimationCannotBeMeasuredTheRawSizeDecides()
    {
        var (plan, _) = await PlanAsync(Probe(102), duplicateRatio: null);

        Assert.Equal("av1_amf", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task NothingIsMeasuredWhenAutoAlreadyChoseSoftwareOrThePresetIsExplicit()
    {
        var (shortPlan, shortMeasurements) = await PlanAsync(Probe(8), duplicateRatio: 0.5);
        var (gpuPlan, gpuMeasurements) = await PlanAsync(Probe(8), duplicateRatio: 0.9, metadata: new Dictionary<string, object?> { ["video.encoderPreset"] = "GpuQuality" });
        var (cpuPlan, cpuMeasurements) = await PlanAsync(Probe(102), duplicateRatio: 0.0, metadata: new Dictionary<string, object?> { ["video.encoderPreset"] = "Cpu" });

        Assert.Equal(0, shortMeasurements);
        Assert.Equal("libsvtav1", shortPlan.Encoder.VideoEncoder);
        Assert.Equal(0, gpuMeasurements);
        Assert.True(gpuPlan.Encoder.UseHardwareEncoder);
        Assert.Equal(0, cpuMeasurements);
        Assert.False(cpuPlan.Encoder.UseHardwareEncoder);
    }

    [Fact]
    public async Task NothingIsMeasuredWhenDecimationIsOffOrAdaptiveSelectionIsDisabled()
    {
        var (_, noDecimation) = await PlanAsync(Probe(102), duplicateRatio: 0.5, options: VideoOptimiserOptions.Default with { EnableFrameDecimation = false });
        var (legacy, legacyMeasurements) = await PlanAsync(Probe(102), duplicateRatio: 0.5, options: VideoOptimiserOptions.Default with { EnableAdaptiveAutoEncoder = false });

        Assert.Equal(0, noDecimation);
        Assert.Equal(0, legacyMeasurements);
        Assert.Equal("libsvtav1", legacy.Encoder.VideoEncoder);
    }

    // ---- bitrate cap on fine (0-255) quantiser scales ---------------------------------------------------------

    [Fact]
    public void DecayPriorFollowsTheQuantiserScale()
    {
        var none = Array.Empty<CrfProbe>();

        Assert.Equal(0.11, CrfSearch.EstimateDecay(none, 40), 6);   // x264 / x265 / NVENC HEVC
        Assert.Equal(0.065, CrfSearch.EstimateDecay(none, 55), 6);  // SVT-AV1
        Assert.Equal(0.025, CrfSearch.EstimateDecay(none, 200), 6); // AMD AV1 (0-255)
    }

    [Fact]
    public void MeasuredDecayOnAFineScaleIsKeptInsteadOfDiscarded()
    {
        // Real av1_amf footage measured 0.021-0.027 per step; the old 0.03 floor threw those away.
        var probes = new[] { new CrfProbe(40, 80, 5_000), new CrfProbe(70, 70, 5_000 * Math.Exp(-0.021 * 30)) };

        Assert.Equal(0.021, CrfSearch.EstimateDecay(probes, 200), 4);
        // On a coarse scale the same pair is implausible and the prior is used.
        Assert.Equal(0.11, CrfSearch.EstimateDecay(probes, 40), 6);
    }

    [Fact]
    public async Task TheBitrateCapReachesItsTargetOnAnAmdAv1Scale()
    {
        // ln(bitrate) falls 0.024 per QP step: to cut the cost 2.5x from QP 40 the search must jump ~38 steps, not ~9.
        double Bitrate(int qp) => 20_000 * Math.Exp(-0.024 * (qp - 40));
        var probesRun = new List<int>();

        Task<CrfProbe?> Model(int qp, CancellationToken token)
        {
            probesRun.Add(qp);
            return Task.FromResult<CrfProbe?>(new CrfProbe(qp, 90 - 0.25 * qp, Bitrate(qp)));
        }

        var first = new CrfSearchOutcome(new CrfProbe(40, 80, Bitrate(40)), true, new[] { new CrfProbe(40, 80, Bitrate(40)) });
        var capped = await CrfSearch.EnforceBitrateCapAsync(first, Bitrate(40) / 2.5, 200, Model, CancellationToken.None);

        // The first jump lands near QP 77 (about 3% short of the cap) and the measured decay finishes it on the second;
        // with an x264-sized prior the two extra probes only reached QP ~57 and shipped a file well above the cap.
        Assert.InRange(probesRun.Count, 1, 2);
        Assert.InRange(probesRun[0], 70, 90);
        Assert.True(capped.Chosen.BitrateKbps <= Bitrate(40) / 2.5, $"chosen QP {capped.Chosen.Crf} still costs {capped.Chosen.BitrateKbps:0} kbps");
    }

    // ---- Auto takes the GPU back when it compresses poorly ---------------------------------------------------

    private sealed class ScriptedSearch : IVideoQualitySearch
    {
        private readonly double _hardwareKbps;
        private readonly double _softwareKbps;

        public ScriptedSearch(double hardwareKbps, double softwareKbps)
        {
            _hardwareKbps = hardwareKbps;
            _softwareKbps = softwareKbps;
        }

        public List<bool> SearchedHardware { get; } = new();

        public Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            SearchedHardware.Add(plan.Encoder.UseHardwareEncoder);
            var kbps = plan.Encoder.UseHardwareEncoder ? _hardwareKbps : _softwareKbps;
            return Task.FromResult<VideoQualitySearchResult?>(new VideoQualitySearchResult(plan.Encoder.UseHardwareEncoder ? 60 : 34, 74, kbps, true, 74));
        }

        public Task<double?> MeasureDuplicateFrameRatioAsync(FilePath source, VideoProbeInfo probe, CancellationToken cancellationToken)
            => Task.FromResult<double?>(0.0);
    }

    private static async Task<(VideoOptimiserPlan Final, ScriptedSearch Search)> RunWithSearchAsync(ScriptedSearch search, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var source = FilePath.TempFile("poor-gpu-test", ".mp4", addUniqueSuffix: true);
        File.WriteAllBytes(source.Value, new byte[4096]);
        var toolchain = new CapturingToolchain();
        // Probe(): 8,000 kbps video; 102 s of 1920x910x30 is a GPU-sized job for the RDNA 4-like capabilities.
        var optimiser = new VideoOptimiser(
            VideoOptimiserOptions.Default with { RequireSmallerSize = false, HardwareOverride = Rdna4, PowerStateOverride = VideoPowerState.Ac },
            toolchain,
            new FixedProbe(Probe(102)),
            search);
        try
        {
            var request = new OptimisationRequest(ItemType.Video, source, metadata: metadata);
            var result = await optimiser.OptimiseAsync(request, new OptimiserExecutionContext(request.RequestId, _ => { }), CancellationToken.None);
            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            if (result.OutputPath is { } output && output.Value != source.Value && File.Exists(output.Value))
            {
                File.Delete(output.Value);
            }

            return (Assert.Single(toolchain.Plans), search);
        }
        finally
        {
            File.Delete(source.Value);
        }
    }

    [Fact]
    public async Task AutoSwitchesToSoftwareWhenTheGpuWouldBarelyCompress()
    {
        // The GPU search predicts ~9,000 kbps for an 8,000 kbps source; software predicts 800.
        var (final, search) = await RunWithSearchAsync(new ScriptedSearch(hardwareKbps: 9_000, softwareKbps: 800));

        Assert.Equal(new[] { true, false }, search.SearchedHardware);
        Assert.False(final.Encoder.UseHardwareEncoder);
        Assert.Equal("libsvtav1", final.Encoder.VideoEncoder);
        Assert.Equal(34, final.Encoder.Quality);
        Assert.False(final.AutoSelectedHardware);
    }

    [Fact]
    public async Task AutoKeepsTheGpuWhenItCompressesWell()
    {
        var (final, search) = await RunWithSearchAsync(new ScriptedSearch(hardwareKbps: 2_500, softwareKbps: 800));

        Assert.Equal(new[] { true }, search.SearchedHardware);
        Assert.Equal("av1_amf", final.Encoder.VideoEncoder);
        Assert.True(final.Encoder.ConstantQuality);
        Assert.True(final.AutoSelectedHardware);
    }

    [Fact]
    public async Task AnExplicitGpuPresetIsNeverTakenBack()
    {
        var (final, search) = await RunWithSearchAsync(new ScriptedSearch(hardwareKbps: 9_000, softwareKbps: 800), new Dictionary<string, object?> { ["video.encoderPreset"] = "GpuQuality" });

        Assert.Equal(new[] { true }, search.SearchedHardware);
        Assert.True(final.Encoder.UseHardwareEncoder);
        Assert.False(final.AutoSelectedHardware);
    }

    // ---- cached SSIMULACRA2 reference ------------------------------------------------------------------------

    private static RasterImage Frame(int width, int height, int seed, int noise = 0)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var baseValue = (x * 3 + y * 2) & 0xFF;
                var jitter = noise == 0 ? 0 : random.Next(-noise, noise + 1);
                pixels[i] = (byte)Math.Clamp(baseValue + jitter, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp((x ^ y) + jitter, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(255 - baseValue + jitter, 0, 255);
                pixels[i + 3] = 255;
            }
        }

        return new RasterImage(width, height, pixels);
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(1280, 720)]
    public void ACachedReferenceScoresExactlyLikeAnUncachedOne(int width, int height)
    {
        var reference = Frame(width, height, seed: 1);
        var candidates = new[] { Frame(width, height, 1, noise: 6), Frame(width, height, 1, noise: 24) };

        using var plain = TileReference.Create(reference);
        using var cached = TileReference.Create(reference);
        cached.EnableCaching();

        foreach (var candidate in candidates)
        {
            var expected = plain.Score(candidate);
            // Twice: the first call prepares the reference, the second reuses it.
            Assert.Equal(expected, cached.Score(candidate), 9);
            Assert.Equal(expected, cached.Score(candidate), 9);
        }

        Assert.True(cached.Score(candidates[0]) > cached.Score(candidates[1]), "more noise must score lower");
    }

    [Fact]
    public void AReferenceBuiltFromAPooledFrameSurvivesTheFrameBeingReturned()
    {
        var source = Frame(640, 360, seed: 2);
        var candidate = Frame(640, 360, 2, noise: 10);

        var pooled = RasterImage.Rent(640, 360);
        Buffer.BlockCopy(source.Pixels, 0, pooled.Pixels, 0, 640 * 360 * 4);
        var tiles = TileReference.Create(pooled);
        var expected = TileReference.Create(source).Score(candidate);

        // The pool reuses the buffer for the next frame: the reference must not still point into it.
        Array.Clear(pooled.Pixels);
        pooled.Dispose();

        Assert.Equal(expected, tiles.Score(candidate), 9);
        tiles.Dispose();
    }

    [Fact]
    public void ATransparentReferenceFallsBackToTheGeneralScore()
    {
        var reference = Frame(320, 240, seed: 3);
        reference.Pixels[3] = 100;
        var candidate = Frame(320, 240, 3, noise: 8);

        using var plain = TileReference.Create(reference);
        using var cached = TileReference.Create(reference);
        cached.EnableCaching();

        Assert.Equal(plain.Score(candidate), cached.Score(candidate), 9);
    }

    [Fact]
    public void TilePixelsReflectWhatTheCacheWouldHold()
    {
        using var small = TileReference.Create(Frame(640, 360, 4));
        using var large = TileReference.Create(Frame(1920, 1080, 4));

        Assert.Equal(640L * 360, small.TilePixels);
        Assert.True(large.TilePixels < 1920L * 1080, "large frames keep a few tiles, not the whole frame");
        Assert.True(large.TilePixels > 0);
    }
}
