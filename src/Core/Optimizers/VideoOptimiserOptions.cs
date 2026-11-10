using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Squish.Core.Settings;
using Squish.Core.Shared;

namespace Squish.Core.Optimizers;

/// <summary>
/// Tunable switches for the video pipeline so callers can match macOS behaviour without
/// hard-coding ffmpeg/gifski arguments inside the optimiser itself.
/// </summary>
public sealed record VideoOptimiserOptions
{
    public static VideoOptimiserOptions Default { get; } = new();

    /// <summary>
    /// Absolute path (or PATH-resolved executable name) for ffmpeg.
    /// </summary>
    public string FfmpegPath { get; init; } = ResolveFfmpegPath();

    /// <summary>
    /// Absolute path for ffprobe. Defaults to PATH lookup.
    /// </summary>
    public string FfprobePath { get; init; } = ResolveFfprobePath();

    /// <summary>
    /// Allows callers to disable expensive metadata probing when not required.
    /// </summary>
    public bool EnableMetadataProbe { get; init; } = true;

    /// <summary>
    /// Optional path to gifski. Required only when GIF export is requested.
    /// </summary>
    public string? GifskiPath { get; init; } = "gifski";

    public bool ForceMp4 { get; init; } = true;

    public bool EnableContainerAwareRemux { get; init; } = true;

    public bool EnableFormatSpecificTuning { get; init; } = true;

    public double MinimumSavingsPercentBeforeReencode { get; init; } = 5d;

    public bool RemoveAudio { get; init; }
        = false;

    public bool CapFps { get; init; } = true;

    public int TargetFps { get; init; } = 60;

    public int MinFps { get; init; } = 24;

    public bool UseHardwareAcceleration { get; init; } = true;

    public string HardwareAccelerationDevice { get; init; } = "d3d11va";

    public string HardwareEncoder { get; init; } = "h264_amf";

    public string SoftwareEncoder { get; init; } = "libx264";

    public string HardwareEncoderHevc { get; init; } = "hevc_amf";

    public string HardwareEncoderAv1 { get; init; } = "av1_amf";

    public string HardwareEncoderVp9 { get; init; } = "vp9_amf";

    public string SoftwareEncoderHevc { get; init; } = "libx265";

    public string SoftwareEncoderAv1 { get; init; } = "libsvtav1";

    public string SoftwareEncoderVp9 { get; init; } = "libvpx-vp9";

    public bool AggressiveQuality { get; init; }
        = false;

    public int SoftwareCrf { get; init; } = 24;

    public int SoftwareCrfHevc { get; init; } = 26;

    public int SoftwareCrfAv1 { get; init; } = 34;

    public int SoftwareCrfVp9 { get; init; } = 33;

    public int HardwareQuality { get; init; } = 23;

    public string SoftwarePresetGentle { get; init; } = "medium";

    public string SoftwarePresetAggressive { get; init; } = "slow";

    public string SoftwarePresetHevcGentle { get; init; } = "medium";

    public string SoftwarePresetHevcAggressive { get; init; } = "slow";

    public string SoftwarePresetAv1Gentle { get; init; } = "6";

    public string SoftwarePresetAv1Aggressive { get; init; } = "5";

    public string SoftwarePresetVp9Gentle { get; init; } = "2";

    public string SoftwarePresetVp9Aggressive { get; init; } = "1";
    public int HardwareQualityHevc { get; init; } = 24;

    public int HardwareQualityAv1 { get; init; } = 28;

    public int HardwareQualityVp9 { get; init; } = 28;

    /// <summary>
    /// Encode with software SVT-AV1 by default: at equal SSIMULACRA2 it is 40-60% smaller than x265 and the GPU encoders,
    /// at about x265's speed. Falls back to HEVC when ffmpeg has no libsvtav1. Choose a GPU <see cref="EncoderPreset"/>
    /// to keep hardware encoding instead.
    /// </summary>
    public bool PreferAv1 { get; init; } = true;

    public bool PreferAv1WhenAggressive { get; init; } = true;

    public bool PreferVp9ForWebm { get; init; } = true;

    /// <summary>Two-pass is only worth it for libvpx-vp9 (see <c>ShouldUseTwoPass</c>); other codecs ignore this.</summary>
    public bool EnableTwoPassEncoding { get; init; } = true;

    /// <summary>
    /// Choose the CRF per video by encoding short samples and scoring them with SSIMULACRA2 instead of using one fixed CRF.
    /// </summary>
    public bool EnableQualitySearch { get; init; } = true;

    /// <summary>Mean SSIMULACRA2 score (weaker half of the sampled frames) a normal-mode encode must reach.</summary>
    public double QualityTargetScore { get; init; } = 74;

    /// <summary>Score floor in aggressive mode.</summary>
    public double QualityTargetScoreAggressive { get; init; } = 64;

    /// <summary>Clips shorter than this are encoded at the fixed CRF - sampling would cost as much as the encode.</summary>
    public double QualitySearchMinimumDurationSeconds { get; init; } = 4;

    public double QualitySearchSampleSeconds { get; init; } = 2;

    public int QualitySearchMaxSamples { get; init; } = 3;

    public int QualitySearchFramesPerSample { get; init; } = 3;

    public int QualitySearchMaxProbes { get; init; } = 4;

    /// <summary>
    /// When the sampled encode would cost at least this multiple of the source's video bitrate, the source is
    /// already efficient and is left alone instead of burning minutes on a result that is not smaller.
    /// The sample is a short, fast-preset encode and overestimates the real full encode by roughly 1.1-1.7x
    /// (measured: Big Buck Bunny 1080p sampled at 0.99x the source but encoded at 0.64x), so this is well above 1:
    /// a ratio near 1 skips sources that would really have shrunk. Hopeless sources sample at 1.7x and up.
    /// </summary>
    public double QualitySearchSkipRatio { get; init; } = 1.5;

    /// <summary>
    /// How much a sampled encode overstates the full encode's bitrate (every sample starts on a keyframe and uses a faster
    /// preset; <see cref="QualitySearchSkipRatio"/> documents 1.1-1.7x). The grain-protection cap is raised by this factor
    /// so it compares like with like.
    /// </summary>
    public double QualitySearchSampleBitrateInflation { get; init; } = 1.3;

    /// <summary>The chosen encode may cost at most this fraction of the source's video bitrate (grain protection).</summary>
    public double QualitySearchMaxBitrateRatio { get; init; } = 0.6;

    public double QualitySearchMaxBitrateRatioAggressive { get; init; } = 0.4;

    /// <summary>
    /// The bitrate cap may push the sampled score at most this far below the target. Beyond that the source is
    /// already efficient and keeping it beats crushing it.
    /// </summary>
    public double QualitySearchCapAllowance { get; init; } = 10;

    public VideoEncoderPreset EncoderPreset { get; init; } = VideoEncoderPreset.Auto;

    /// <summary>
    /// "Auto" weighs the job (resolution x frame rate x duration), the GPU's encoder generation and the power source,
    /// staying on software SVT-AV1 for small jobs and moving large ones to the GPU. Off restores the old behaviour of
    /// always choosing software AV1 when it is available.
    /// </summary>
    public bool EnableAdaptiveAutoEncoder { get; init; } = true;

    /// <summary>On AC power, jobs up to this many megapixels of work (~15 s of 1080p30) always use software.</summary>
    public double AutoCpuMaxMegapixels { get; init; } = 1_000;

    /// <summary>Jobs above this many megapixels (~30 s of 1080p30 is 1,900) go to the GPU with latest-generation AV1 silicon.</summary>
    public double AutoGpuMinMegapixelsFlagship { get; init; } = 1_900;

    /// <summary>Threshold for first-generation hardware AV1 (~30 s of 1080p60).</summary>
    public double AutoGpuMinMegapixelsAv1 { get; init; } = 3_700;

    /// <summary>Threshold for hardware HEVC without AV1 (~60 s of 1080p60), where the GPU's files are 25-40% larger.</summary>
    public double AutoGpuMinMegapixelsModern { get; init; } = 7_500;

    /// <summary>Threshold for GPUs that only encode H.264.</summary>
    public double AutoGpuMinMegapixelsLegacy { get; init; } = 15_000;

    /// <summary>On battery (or battery saver) a GPU that can encode HEVC or better takes any job from this size up (~8 s of 1080p30).</summary>
    public double AutoBatteryGpuMinMegapixels { get; init; } = 500;

    /// <summary>
    /// When Auto chose the GPU, the perceptual search predicts what the GPU will actually produce. If that is more than this
    /// fraction of the source's own video bitrate, the GPU is barely compressing (some content, notably flat gradients, needs
    /// a hardware AV1 encoder to run at very low quantisers) and Auto switches to software instead. Explicit GPU presets are
    /// never overridden. Measured on real clips with an RDNA 4 GPU: footage the GPU handles well lands at 12-26% of the
    /// source (and often beats software), while the cases where software was 4-5x smaller landed at 56% and above.
    /// </summary>
    public double AutoHardwareMaxSourceBitrateRatio { get; init; } = 0.4;

    /// <summary>Pins the power source instead of asking Windows; used by tests and by hosts that want a fixed behaviour.</summary>
    public VideoPowerState? PowerStateOverride { get; init; }

    /// <summary>
    /// Minimum duration (in seconds) before two-pass encoding is considered. Short clips finish faster with a single pass.
    /// Set to 0 to always allow two-pass.
    /// </summary>
    public double TwoPassMinimumDurationSeconds { get; init; } = 45d;

    public bool EnableSceneCutAwareBitrate { get; init; } = true;

    public int SoftwareLookaheadFrames { get; init; } = 32;

    public int HardwareLookaheadFrames { get; init; } = 16;

    public int SceneCutThreshold { get; init; } = 35;

    public double HardwareBitrateReductionRatio { get; init; } = 0.7;

    public int HardwareBitrateFloorKbps { get; init; } = 800;

    public int HardwareBitrateCeilingKbps { get; init; } = 45000;

    public double HardwareBitrateMaxrateHeadroom { get; init; } = 1.0;

    public double HardwareBitrateBufferMultiplier { get; init; } = 2.0;

    public double HardwareMinimumSavingsPercent { get; init; } = 18d;

    public double HardwareBitrateRetryReductionRatio { get; init; } = 0.75;

    public int HardwareBitrateRetryLimit { get; init; } = 2;

    public bool EnableFrameDecimation { get; init; } = true;

    /// <summary>
    /// A GPU encode drops frame decimation (a CPU filter that forces every frame through system memory) when the sampled
    /// footage would lose fewer than this fraction of its frames to it.
    /// </summary>
    public double ZeroCopyMaxDuplicateFrameRatio { get; init; } = 0.03;

    public double FrameDecimationHighThreshold { get; init; } = 64;

    public double FrameDecimationLowThreshold { get; init; } = 15;

    public double FrameDecimationMaxDifference { get; init; } = 15;

    /// <summary>Loudness normalisation re-encodes audio and changes how it sounds, so it is strictly opt-in.</summary>
    public bool EnableAudioNormalization { get; init; }

    /// <summary>Audio streams above this bitrate (kbps) are re-encoded instead of copied.</summary>
    public int AudioCopyMaxBitrateKbps { get; init; } = 320;

    public int AudioTargetBitrateKbps { get; init; } = 160;

    public string AudioEncoderAac { get; init; } = "aac";

    public string AudioEncoderOpus { get; init; } = "libopus";

    public int AudioDownmixChannels { get; init; } = 2;

    public double LoudnessTargetIntegrated { get; init; } = -16.0;

    public double LoudnessTargetTruePeak { get; init; } = -1.5;

    public double LoudnessTargetLra { get; init; } = 11.0;

    public AnimatedExportFormat PreferredAnimatedExport { get; init; } = AnimatedExportFormat.Gif;

    public bool PreferAnimatedWebpForHighQuality { get; init; } = true;

    public VideoHardwareCapabilities? HardwareOverride { get; init; }
        = null;

    public bool ProbeHardwareCapabilities { get; init; } = true;

    public bool PreserveTimestamps { get; init; } = true;

    public bool RequireSmallerSize { get; init; } = true;

    public bool StripMetadata { get; init; }
        = false;

    public bool PreserveColorMetadata { get; init; } = true;

    public string DefaultVideoExtension { get; init; } = "mp4";

    public bool EnableGifExport { get; init; } = true;

    public int GifMaxWidth { get; init; } = 720;

    public int GifFps { get; init; } = 18;

    public int GifQuality { get; init; } = 85;

    /// <summary>
    /// Ensures resize filters keep dimensions divisible by two (required by many codecs).
    /// </summary>
    public bool EnforceEvenDimensions { get; init; } = true;

    /// <summary>
    /// Allows feature flags (CLI, UI, automation) to request GIF export via metadata.
    /// </summary>
    public IReadOnlyCollection<string> GifTriggers { get; init; } = new[] { "gif", "animated" };

    private static string ResolveFfmpegPath()
    {
        return ResolveTool(
            new[] { "SQUISH_FFMPEG", "FFMPEG_EXECUTABLE" },
            new[]
            {
                new[] { "tools", "ffmpeg", "bin", "ffmpeg.exe" },
                new[] { "tools", "ffmpeg", "ffmpeg.exe" }
            },
            "ffmpeg.exe",
            "ffmpeg");
    }

    private static string ResolveFfprobePath()
    {
        return ResolveTool(
            new[] { "SQUISH_FFPROBE", "FFPROBE_EXECUTABLE" },
            new[]
            {
                new[] { "tools", "ffmpeg", "bin", "ffprobe.exe" },
                new[] { "tools", "ffmpeg", "ffprobe.exe" }
            },
            "ffprobe.exe",
            "ffprobe");
    }

    private static string ResolveTool(IEnumerable<string> environmentVariables, IReadOnlyList<string[]> bundledSegments, string fileName, string defaultCommand)
    {
        var envPath = ResolveFromEnvironment(environmentVariables);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath!;
        }

        var baseDir = GetBaseDirectory();
        foreach (var segments in bundledSegments)
        {
            var candidate = ToolLocator.EnumeratePossibleFiles(baseDir, segments).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate!;
            }
        }

        foreach (var localCandidate in EnumerateLocalToolCandidates(fileName))
        {
            return localCandidate;
        }

        // Bundled folder, per-user folder and PATH, using the platform's executable naming.
        var toolName = Path.GetFileNameWithoutExtension(fileName);
        var discovered = ExternalToolResolver.Resolve(toolName, "ffmpeg");
        if (!string.IsNullOrWhiteSpace(discovered))
        {
            return discovered!;
        }

        // Never throw from a static initialiser: a missing tool must surface as a failed optimisation with a
        // readable message, not as a crash that takes image and PDF optimisation down with it.
        return defaultCommand;
    }

    private static string? ResolveFromEnvironment(IEnumerable<string> variableNames)
    {
        foreach (var variable in variableNames)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(value);
            if (File.Exists(expanded))
            {
                return expanded;
            }

            var resolved = ToolLocator.ResolveOnPath(expanded);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            return expanded;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLocalToolCandidates(string fileName)
    {
        foreach (var root in GetLocalToolRoots())
        {
            var candidate = Path.Combine(root, fileName);
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> GetLocalToolRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData!, "Squish", "bin");
            yield return Path.Combine(localAppData!, "Squish", "bin", "x64");
        }

        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            yield return Path.Combine(roamingAppData!, "Squish", "bin");
            yield return Path.Combine(roamingAppData!, "Squish", "bin", "x64");
        }
    }

    private static string? GetBaseDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.CurrentDirectory;
        }

        return baseDir;
    }
}