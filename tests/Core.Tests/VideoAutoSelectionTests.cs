using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Optimizers;
using Squish.Core.Settings;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>GPU/CPU selection for the Auto preset, hardware probing and the vendor encoder profiles (no ffmpeg needed).</summary>
[Collection("VideoTools")]
public sealed class VideoAutoSelectionTests
{
    public VideoAutoSelectionTests(VideoToolchainFixture tools) => _ = tools ?? throw new ArgumentNullException(nameof(tools));

    // ---- hardware shapes -------------------------------------------------------------------------------------

    private static VideoHardwareCapabilities Caps(
        bool nvenc = false, bool amf = false, bool qsv = false,
        bool av1Nvenc = false, bool av1Amf = false, bool av1Qsv = false,
        bool hevcNvenc = false, bool hevcAmf = false, bool hevcQsv = false,
        bool flagship = false)
        => new(nvenc, amf, qsv, true, av1Nvenc, av1Amf, av1Qsv, hevcNvenc, hevcAmf, hevcQsv) { FlagshipAv1Silicon = flagship };

    private static readonly VideoHardwareCapabilities Rtx3080 = Caps(nvenc: true, hevcNvenc: true);
    private static readonly VideoHardwareCapabilities Rtx4070 = Caps(nvenc: true, hevcNvenc: true, av1Nvenc: true);
    private static readonly VideoHardwareCapabilities Rtx5080 = Caps(nvenc: true, hevcNvenc: true, av1Nvenc: true, flagship: true);
    private static readonly VideoHardwareCapabilities Rdna4 = Caps(amf: true, hevcAmf: true, av1Amf: true);
    private static readonly VideoHardwareCapabilities IntelArc = Caps(qsv: true, hevcQsv: true, av1Qsv: true);
    private static readonly VideoHardwareCapabilities Pascal = Caps(nvenc: true);

    // ---- hardware probing ------------------------------------------------------------------------------------

    private static readonly string[] AllEncoders =
    {
        "V....D h264_nvenc           NVIDIA NVENC H.264 encoder",
        "V....D hevc_nvenc           NVIDIA NVENC hevc encoder",
        "V....D av1_nvenc            NVIDIA NVENC av1 encoder",
        "V....D h264_amf             AMD AMF H.264 Encoder",
        "V....D hevc_amf             AMD AMF HEVC encoder",
        "V....D av1_amf              AMD AMF AV1 encoder",
        "V..... h264_qsv             H.264 (Intel Quick Sync Video acceleration)",
        "V..... hevc_qsv             HEVC (Intel Quick Sync Video acceleration)",
        "V..... av1_qsv              AV1 (Intel Quick Sync Video acceleration)",
        "V..... vp9_qsv              VP9 video (Intel Quick Sync Video acceleration)",
        "V..... libsvtav1            SVT-AV1 encoder"
    };

    [Fact]
    public void AnRtx3080IsNotReportedAsHavingAnAv1Encoder()
    {
        // ffmpeg lists av1_nvenc in every build; only a real encode shows the silicon cannot do it.
        var caps = VideoHardwareDetector.BuildCapabilities(
            AllEncoders, new[] { "d3d11va" }, new[] { "NVIDIA GeForce RTX 3080" },
            hasVendorLibrary: name => name.StartsWith("nvcuda", StringComparison.OrdinalIgnoreCase),
            encodesOnHardware: encoder => encoder is "h264_nvenc" or "hevc_nvenc");

        Assert.True(caps.SupportsNvenc);
        Assert.True(caps.SupportsHevcNvenc);
        Assert.False(caps.SupportsAv1Nvenc);
        Assert.False(caps.SupportsAv1Hardware);
        Assert.Equal(GpuEncoderTier.Modern, caps.EncoderTier);
    }

    [Fact]
    public void IntelQuickSyncIsFoundWithoutAnyLegacyDllInSystem32()
    {
        var caps = VideoHardwareDetector.BuildCapabilities(
            AllEncoders, new[] { "d3d11va", "qsv" }, new[] { "Intel(R) Arc(TM) B580 Graphics" },
            hasVendorLibrary: _ => false,
            encodesOnHardware: encoder => encoder.EndsWith("_qsv", StringComparison.Ordinal) && encoder != "vp9_qsv");

        Assert.True(caps.SupportsQsv);
        Assert.True(caps.SupportsAv1Qsv);
        Assert.True(caps.SupportsHevcQsv);
        Assert.False(caps.CanEncodeVp9Qsv);
        Assert.Equal(GpuEncoderTier.Flagship, caps.EncoderTier);
    }

    [Fact]
    public void OnlyEncodersOfGpusThatCouldBePresentAreProbed()
    {
        var probed = new List<string>();
        VideoHardwareDetector.BuildCapabilities(
            AllEncoders, Array.Empty<string>(), new[] { "NVIDIA GeForce RTX 4070" },
            hasVendorLibrary: name => name.StartsWith("nvcuda", StringComparison.OrdinalIgnoreCase),
            encodesOnHardware: encoder =>
            {
                lock (probed)
                {
                    probed.Add(encoder);
                }

                return true;
            });

        Assert.Equal(new[] { "av1_nvenc", "h264_nvenc", "hevc_nvenc" }, probed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void WhenAdaptersCannotBeReadQuickSyncIsStillTried()
    {
        var caps = VideoHardwareDetector.BuildCapabilities(
            AllEncoders, Array.Empty<string>(), Array.Empty<string>(),
            hasVendorLibrary: _ => false,
            encodesOnHardware: encoder => encoder == "hevc_qsv");

        Assert.True(caps.SupportsHevcQsv);
        Assert.Equal(GpuEncoderTier.Modern, caps.EncoderTier);
    }

    [Fact]
    public void AProbeThatThrowsCountsAsUnsupportedInsteadOfFailingDetection()
    {
        var caps = VideoHardwareDetector.BuildCapabilities(
            AllEncoders, Array.Empty<string>(), new[] { "AMD Radeon RX 7800 XT" },
            hasVendorLibrary: name => name.StartsWith("amfrt", StringComparison.OrdinalIgnoreCase),
            encodesOnHardware: encoder => encoder == "av1_amf" ? throw new InvalidOperationException("driver crashed") : encoder.EndsWith("_amf", StringComparison.Ordinal));

        Assert.True(caps.SupportsHevcAmf);
        Assert.False(caps.SupportsAv1Amf);
    }

    [Fact]
    public void CapabilitiesSurviveTheRoundTripThroughTheDiskCache()
    {
        var caps = new VideoHardwareCapabilities(true, false, true, true, true, false, true, true, false, true, SupportsSoftwareAv1: true)
        {
            SupportsH264Nvenc = true,
            SupportsH264Qsv = false,
            FlagshipAv1Silicon = true
        };

        var restored = JsonSerializer.Deserialize<VideoHardwareCapabilities>(JsonSerializer.Serialize(caps));

        Assert.Equal(caps, restored);
        Assert.Equal(GpuEncoderTier.Flagship, restored!.EncoderTier);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", true)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", true)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    [InlineData("NVIDIA RTX 6000 Ada Generation", false)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    public void FlagshipSiliconIsRecognisedByAdapterName(string adapter, bool expected)
        => Assert.Equal(expected, GpuAdapters.IsFlagshipAv1(new[] { adapter }));

    // ---- workload and policy ---------------------------------------------------------------------------------

    private static VideoProbeInfo Probe(double duration = 10, int width = 1920, int height = 1080, double fps = 30, string codec = "h264")
    {
        var video = new VideoStreamInfo(codec, "", "yuv420p", "bt709", "bt709", "bt709", "tv", width, height, 8_000_000, fps, false, false);
        var audio = new AudioStreamInfo("aac", "", 2, 48_000, 128_000);
        return new VideoProbeInfo("mov,mp4,m4a,3gp,3g2,mj2", "QuickTime / MOV", duration, 8_200_000, 10_000_000, video, audio);
    }

    [Fact]
    public void WorkloadIsPixelsTimesFramesInMegapixels()
    {
        var workload = VideoWorkload.Measure(Probe(duration: 30, fps: 60), capFps: false, targetFps: 60, null, null, null);

        Assert.NotNull(workload);
        Assert.Equal(1920d * 1080 * 60 * 30 / 1e6, workload!.Value.TotalMegapixels, 3);
    }

    [Fact]
    public void WorkloadHonoursTheFrameRateCapResizeAndSpeed()
    {
        var plain = VideoWorkload.Measure(Probe(duration: 20, fps: 120), capFps: false, targetFps: 60, null, null, null)!.Value.TotalMegapixels;
        var capped = VideoWorkload.Measure(Probe(duration: 20, fps: 120), capFps: true, targetFps: 60, null, null, null)!.Value.TotalMegapixels;
        var half = VideoWorkload.Measure(Probe(duration: 20, fps: 120), capFps: false, targetFps: 60, 960, 540, null)!.Value.TotalMegapixels;
        var doubleSpeed = VideoWorkload.Measure(Probe(duration: 20, fps: 120), capFps: false, targetFps: 60, null, null, 2.0)!.Value.TotalMegapixels;

        Assert.Equal(plain / 2, capped, 3);
        Assert.Equal(plain / 4, half, 3);
        Assert.Equal(plain / 2, doubleSpeed, 3);
    }

    [Fact]
    public void WorkloadIsUnknownWithoutGeometryOrDuration()
    {
        Assert.Null(VideoWorkload.Measure(null, true, 60, null, null, null));
        Assert.Null(VideoWorkload.Measure(Probe(duration: 0), true, 60, null, null, null));
        Assert.Null(VideoWorkload.Measure(Probe(width: 0), true, 60, null, null, null));
    }

    private static AutoEncoderDecision Decide(VideoHardwareCapabilities caps, double megapixels, VideoPowerState power = VideoPowerState.Ac, VideoOptimiserOptions? options = null)
        => VideoAutoEncoderPolicy.Decide(options ?? VideoOptimiserOptions.Default, caps, new VideoWorkload(megapixels), power);

    [Theory]
    [InlineData(500)]
    [InlineData(930)]
    public void ShortClipsStayInSoftwareOnAcPowerOnAnyGpu(double megapixels)
    {
        Assert.False(Decide(Rtx5080, megapixels).UseHardware);
        Assert.False(Decide(Rtx3080, megapixels).UseHardware);
    }

    [Fact]
    public void ThresholdsGrowAsTheGpuEncoderGetsOlder()
    {
        // 3,000 MP is ~25 s of 1080p60: past the flagship threshold, below first-generation AV1 and HEVC-only ones.
        Assert.True(Decide(Rtx5080, 3_000).UseHardware);
        Assert.False(Decide(Rtx4070, 3_000).UseHardware);
        Assert.False(Decide(Rtx3080, 3_000).UseHardware);

        Assert.True(Decide(Rtx4070, 4_000).UseHardware);
        Assert.False(Decide(Rtx3080, 4_000).UseHardware);

        Assert.True(Decide(Rtx3080, 8_000).UseHardware);
        Assert.False(Decide(Pascal, 8_000).UseHardware);
        Assert.True(Decide(Pascal, 16_000).UseHardware);
    }

    [Fact]
    public void ABatteryMovesAnyRealVideoToTheGpuButNotATinyOne()
    {
        Assert.True(Decide(Rtx3080, 600, VideoPowerState.Battery).UseHardware);
        Assert.True(Decide(Rtx3080, 600, VideoPowerState.BatterySaver).UseHardware);
        Assert.False(Decide(Rtx3080, 600, VideoPowerState.Ac).UseHardware);
        Assert.False(Decide(Rtx3080, 100, VideoPowerState.Battery).UseHardware);

        // An H.264-only GPU is not worth the larger files just to save power.
        Assert.False(Decide(Pascal, 600, VideoPowerState.Battery).UseHardware);
    }

    [Fact]
    public void WithoutAWorkingGpuOrKnownWorkloadOrWhenDisabledItStaysInSoftware()
    {
        Assert.False(Decide(VideoHardwareCapabilities.CpuOnly, 1e6).UseHardware);
        Assert.False(VideoAutoEncoderPolicy.Decide(VideoOptimiserOptions.Default, Rtx5080, null, VideoPowerState.Battery).UseHardware);
        Assert.False(Decide(Rtx5080, 1e6, options: VideoOptimiserOptions.Default with { EnableAdaptiveAutoEncoder = false }).UseHardware);
        Assert.False(Decide(Rtx5080, 1e6, options: VideoOptimiserOptions.Default with { UseHardwareAcceleration = false }).UseHardware);
    }

    // ---- planning --------------------------------------------------------------------------------------------

    private static async Task<VideoOptimiserPlan> PlanAsync(VideoProbeInfo probe, VideoHardwareCapabilities hardware, VideoPowerState power = VideoPowerState.Ac, IReadOnlyDictionary<string, object?>? metadata = null, string extension = ".mp4", VideoOptimiserOptions? options = null)
    {
        var source = FilePath.TempFile("auto-select-test", extension, addUniqueSuffix: true);
        File.WriteAllBytes(source.Value, new byte[4096]);
        var toolchain = new CapturingToolchain();
        var optimiser = new VideoOptimiser(
            (options ?? VideoOptimiserOptions.Default) with { RequireSmallerSize = false, EnableQualitySearch = false, HardwareOverride = hardware, PowerStateOverride = power },
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

    private static Dictionary<string, object?> Meta(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public async Task AutoStaysOnSoftwareAv1ForAShortClipEvenWithTheFastestGpu()
    {
        var plan = await PlanAsync(Probe(duration: 8), Rtx5080);

        Assert.False(plan.Encoder.UseHardwareEncoder);
        Assert.Equal("libsvtav1", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task AutoMovesALongRecordingToTheGpusAv1Encoder()
    {
        var plan = await PlanAsync(Probe(duration: 180, fps: 60), Rdna4);

        Assert.True(plan.Encoder.UseHardwareEncoder);
        Assert.Equal(VideoCodec.Av1, plan.Encoder.Codec);
        Assert.Equal("av1_amf", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task AutoUsesHardwareHevcOnAGpuWithoutAv1WhenTheJobIsHuge()
    {
        var plan = await PlanAsync(Probe(duration: 300, fps: 60), Rtx3080);

        Assert.Equal("hevc_nvenc", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task AutoSavesBatteryByUsingTheGpuForAMediumClip()
    {
        var onBattery = await PlanAsync(Probe(duration: 20), Rtx3080, VideoPowerState.Battery);
        var pluggedIn = await PlanAsync(Probe(duration: 20), Rtx3080, VideoPowerState.Ac);

        Assert.Equal("hevc_nvenc", onBattery.Encoder.VideoEncoder);
        Assert.Equal("libsvtav1", pluggedIn.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task AutoWithAForcedCodecDoesNotSlipOntoTheGpuForASmallClip()
    {
        // ProRes -> HEVC used to take whatever GPU encoder existed, even for a few seconds of footage.
        var plan = await PlanAsync(Probe(duration: 4, codec: "prores"), Rtx3080, extension: ".mov");

        Assert.Equal(VideoCodec.Hevc, plan.Encoder.Codec);
        Assert.False(plan.Encoder.UseHardwareEncoder);
    }

    [Fact]
    public async Task TheOldBehaviourIsAvailableByTurningAdaptiveSelectionOff()
    {
        var options = VideoOptimiserOptions.Default with { EnableAdaptiveAutoEncoder = false };
        var plan = await PlanAsync(Probe(duration: 600, fps: 60), Rtx5080, options: options);

        Assert.Equal("libsvtav1", plan.Encoder.VideoEncoder);
    }

    [Fact]
    public async Task ExplicitPresetsAreNotAffectedByTheAutoPolicy()
    {
        var cpu = await PlanAsync(Probe(duration: 600, fps: 60), Rtx5080, metadata: Meta(("video.encoderPreset", "Cpu")));
        var gpu = await PlanAsync(Probe(duration: 3), Rtx5080, metadata: Meta(("video.encoderPreset", "GpuQuality")));

        Assert.False(cpu.Encoder.UseHardwareEncoder);
        Assert.True(gpu.Encoder.UseHardwareEncoder);
    }

    // ---- vendor encoder profiles -----------------------------------------------------------------------------

    private static Task<VideoOptimiserPlan> GpuPlanAsync(VideoHardwareCapabilities hardware, string preset = "GpuQuality")
        => PlanAsync(Probe(), hardware, metadata: Meta(("video.encoderPreset", preset)));

    [Fact]
    public async Task NvencUsesThePerceptualProfileAndNoLegacyRateControl()
    {
        var hevc = (await GpuPlanAsync(Rtx3080)).VideoCodecArguments.ToList();

        Assert.DoesNotContain("vbr_hq", hevc);
        Assert.Equal("vbr", hevc[hevc.IndexOf("-rc") + 1]);
        Assert.Equal("qres", hevc[hevc.IndexOf("-multipass") + 1]);
        Assert.Equal("1", hevc[hevc.IndexOf("-spatial-aq") + 1]);
        Assert.Equal("1", hevc[hevc.IndexOf("-temporal-aq") + 1]);
        Assert.Equal("middle", hevc[hevc.IndexOf("-b_ref_mode") + 1]);
        Assert.Equal("p7", hevc[hevc.IndexOf("-preset") + 1]);
        Assert.Equal("hq", hevc[hevc.IndexOf("-tune") + 1]);
        Assert.Contains("-rc-lookahead", hevc);
        Assert.DoesNotContain("-sc_threshold", hevc);
    }

    [Fact]
    public async Task NvencAv1SkipsTemporalAqAndTheFlagshipChipGetsTheUltraHighQualityTune()
    {
        var ada = (await GpuPlanAsync(Rtx4070)).VideoCodecArguments.ToList();
        var blackwell = (await GpuPlanAsync(Rtx5080)).VideoCodecArguments.ToList();

        Assert.Contains("av1_nvenc", ada);
        Assert.DoesNotContain("-temporal-aq", ada);
        Assert.Equal("hq", ada[ada.IndexOf("-tune") + 1]);
        Assert.Equal("uhq", blackwell[blackwell.IndexOf("-tune") + 1]);
    }

    [Fact]
    public async Task GpuSimpleIsTheFastNvencProfile()
    {
        var args = (await GpuPlanAsync(Rtx3080, "GpuSimple")).VideoCodecArguments.ToList();

        Assert.Equal("p4", args[args.IndexOf("-preset") + 1]);
        Assert.Equal("disabled", args[args.IndexOf("-multipass") + 1]);
    }

    [Fact]
    public async Task AmfAndQsvDoNotReceiveNvencOnlyOrX264OnlyFlags()
    {
        foreach (var hardware in new[] { Rdna4, IntelArc })
        {
            var args = (await GpuPlanAsync(hardware)).VideoCodecArguments.ToList();

            Assert.DoesNotContain("-rc-lookahead", args);
            Assert.DoesNotContain("-sc_threshold", args);
            Assert.DoesNotContain("-cq", args);
        }
    }

    [Fact]
    public async Task AmfWithoutABitrateTargetUsesConstantQpInsteadOfTheIgnoredCqFlag()
    {
        var h264Only = Caps(amf: true);
        var args = (await GpuPlanAsync(h264Only, "GpuCqp")).VideoCodecArguments.ToList();

        Assert.Contains("h264_amf", args);
        Assert.Equal("cqp", args[args.IndexOf("-rc") + 1]);
        Assert.Contains("-qp_i", args);
        Assert.DoesNotContain("-cq", args);
    }

    [Fact]
    public async Task Av1AmfConstantQpStartsFromAnAv1SizedQuantiser()
    {
        var args = (await PlanAsync(Probe(), Rdna4, metadata: Meta(("video.encoderPreset", "GpuCqp")))).VideoCodecArguments.ToList();

        Assert.Contains("av1_amf", args);
        // AV1 QPs run 0-255; an H.264-sized value would be near-lossless and enormous.
        Assert.True(int.Parse(args[args.IndexOf("-qp_i") + 1], System.Globalization.CultureInfo.InvariantCulture) >= 60);
    }

    [Fact]
    public async Task Av1AmfAvoidsPreAnalysisAndContextAdaptiveQuantisation()
    {
        // Pre-analysis stalls av1_amf; -aq_mode caq saves bits per quantiser but loses quality per quantiser and came out
        // larger at equal SSIMULACRA2 on real clips. Neither belongs in the profile.
        var args = (await GpuPlanAsync(Rdna4)).VideoCodecArguments.ToList();

        Assert.Contains("av1_amf", args);
        Assert.DoesNotContain("-preanalysis", args);
        Assert.DoesNotContain("-aq_mode", args);
        Assert.DoesNotContain("-bf", args);
    }

    [Theory]
    [InlineData("GpuQuality")]
    [InlineData("GpuSimple")]
    [InlineData("GpuCqp")]
    public async Task AmfHevcIsNotAskedForBFramesItCannotEncode(string preset)
    {
        var args = (await GpuPlanAsync(Caps(amf: true, hevcAmf: true), preset)).VideoCodecArguments.ToList();

        Assert.Contains("hevc_amf", args);
        Assert.DoesNotContain("-bf", args);
        Assert.DoesNotContain("-qp_b", args);
        Assert.DoesNotContain("-aq_mode", args);
    }

    [Fact]
    public async Task QsvUsesIcqQualityAndItsOwnPresetNames()
    {
        var plan = await PlanAsync(Probe(), Caps(qsv: true, hevcQsv: true), metadata: Meta(("video.encoderPreset", "GpuCqp")));
        var args = plan.VideoCodecArguments.ToList();

        Assert.Equal("hevc_qsv", plan.Encoder.VideoEncoder);
        Assert.Contains("-global_quality", args);
        Assert.Equal("slower", args[args.IndexOf("-preset") + 1]);
        Assert.Contains("-adaptive_b", args);
    }

    [Fact]
    public void IntelQsvAv1HasAPerceptualSearchRange()
    {
        var selection = VideoEncoderSelection.Hardware(VideoCodec.Av1, "av1_qsv", "mkv", "p010le", "qsv", null, 30, Array.Empty<string>(), false, 0, false);

        Assert.True(HardwareConstantQuality.TryGetSearchRange(selection, out var range));
        Assert.True(range.Min < range.Start && range.Start < range.Max);
        Assert.Equal(new[] { "-global_quality", "30" }, HardwareConstantQuality.Arguments(selection, 30));
    }

    [Fact]
    public async Task AmdHevcBitrateProfileUsesARateControlNameAmfAccepts()
    {
        // "vbr" is not an AMF rate-control mode ("Unable to parse option value"); the peak-constrained one is.
        var plan = await PlanAsync(Probe(), Rdna4.EncoderTier == GpuEncoderTier.Av1 ? Caps(amf: true, hevcAmf: true) : Rdna4, metadata: Meta(("video.encoderPreset", "GpuQuality")));
        var selection = plan.Encoder;

        Assert.Equal("hevc_amf", selection.VideoEncoder);
        Assert.NotEqual("vbr", selection.RateControlValueOverride);
    }

    // ---- zero-copy GPU pipeline ------------------------------------------------------------------------------

    private static (VideoOptimiser Optimiser, VideoOptimiserOptions Options) OptimiserFor(VideoHardwareCapabilities hardware)
    {
        var options = VideoOptimiserOptions.Default with { HardwareOverride = hardware, PowerStateOverride = VideoPowerState.Ac };
        return (new VideoOptimiser(options, new CapturingToolchain(), new FixedProbe(Probe())), options);
    }

    private static VideoQualitySearchResult SearchWith(double? duplicates) => new(30, 74, 5_000, true, 74, duplicates);

    [Fact]
    public async Task FootageWithAlmostNoDuplicateFramesKeepsTheGpuPipelineZeroCopy()
    {
        var plan = await GpuPlanAsync(Rtx3080);
        Assert.Contains(plan.Filters, filter => filter.StartsWith("mpdecimate", StringComparison.Ordinal));
        var (optimiser, options) = OptimiserFor(Rtx3080);

        var gated = optimiser.ApplyZeroCopyGate(plan, SearchWith(0.004));

        Assert.False(gated.RequiresFilters);
        Assert.Equal(plan.Filters, gated.FiltersBeforeZeroCopy);

        var args = new ExternalVideoToolchain(options).BuildTranscodeArguments(gated, FilePath.From(Path.Combine(Path.GetTempPath(), "out.mp4")), includeAudio: false);
        Assert.Equal("cuda", args[args.IndexOf("-hwaccel_output_format") + 1]);
        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-pix_fmt", args);
    }

    [Theory]
    [InlineData(0.03)]
    [InlineData(0.4)]
    public async Task FootageThatDecimationActuallyShrinksKeepsTheFilterAndTheCpuPath(double duplicates)
    {
        var plan = await GpuPlanAsync(Rtx3080);
        var (optimiser, options) = OptimiserFor(Rtx3080);

        var gated = optimiser.ApplyZeroCopyGate(plan, SearchWith(duplicates));

        Assert.Same(plan, gated);
        var args = new ExternalVideoToolchain(options).BuildTranscodeArguments(gated, FilePath.From(Path.Combine(Path.GetTempPath(), "out.mp4")), includeAudio: false);
        Assert.DoesNotContain("-hwaccel_output_format", args);
        Assert.Contains("-vf", args);
    }

    [Fact]
    public async Task TheZeroCopyGateNeverTouchesSoftwareOrUnmeasuredPlans()
    {
        var software = await PlanAsync(Probe(duration: 5), Rtx3080);
        var gpu = await GpuPlanAsync(Rtx3080);
        var (optimiser, _) = OptimiserFor(Rtx3080);

        Assert.Same(software, optimiser.ApplyZeroCopyGate(software, SearchWith(0)));
        Assert.Same(gpu, optimiser.ApplyZeroCopyGate(gpu, SearchWith(null)));
    }

    [Fact]
    public async Task AFailedZeroCopyEncodeRetriesTheGpuWithThePlainProfileAndTheOriginalFilters()
    {
        var plan = await GpuPlanAsync(Rtx3080);
        var (optimiser, options) = OptimiserFor(Rtx3080);
        var gated = optimiser.ApplyZeroCopyGate(plan, SearchWith(0.0));

        var retry = VideoOptimiser.BuildConservativeHardwarePlan(options, gated);

        Assert.NotNull(retry);
        Assert.Equal(plan.Filters, retry!.Filters);
        Assert.Null(retry.FiltersBeforeZeroCopy);
        var args = retry.VideoCodecArguments.ToList();
        Assert.DoesNotContain("-multipass", args);
        Assert.DoesNotContain("-spatial-aq", args);
        Assert.Contains("hevc_nvenc", args);
        Assert.Equal("hvc1", args[args.IndexOf("-tag:v") + 1]);
    }

    [Fact]
    public async Task SoftwarePlansHaveNoConservativeHardwareRetry()
    {
        var software = await PlanAsync(Probe(duration: 5), Rtx3080);

        Assert.Null(VideoOptimiser.BuildConservativeHardwarePlan(VideoOptimiserOptions.Default, software));
    }

    // ---- settings --------------------------------------------------------------------------------------------

    [Fact]
    public void TheSavedEncoderPresetReachesTheVideoOptions()
    {
        var directory = Directory.CreateTempSubdirectory("squish-settings-");
        try
        {
            using var store = new SettingsStore(directory.FullName);
            store.Set(SettingsRegistry.VideoEncoderPresetPreference, VideoEncoderPreset.GpuQuality);

            var options = VideoOptimiserOptionsFactory.Apply(VideoOptimiserOptions.Default, store);

            Assert.Equal(VideoEncoderPreset.GpuQuality, options.EncoderPreset);
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // the settings watcher may still hold the folder briefly
            }
        }
    }

    // ---- fakes -----------------------------------------------------------------------------------------------

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
