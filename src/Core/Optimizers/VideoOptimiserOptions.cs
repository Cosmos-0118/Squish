using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

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

    public int SoftwareCrf { get; init; } = 26;

    public int SoftwareCrfHevc { get; init; } = 24;

    public int SoftwareCrfAv1 { get; init; } = 32;

    public int SoftwareCrfVp9 { get; init; } = 30;

    public int HardwareQuality { get; init; } = 23;

    public string SoftwarePresetGentle { get; init; } = "faster";

    public string SoftwarePresetAggressive { get; init; } = "slower";

    public string SoftwarePresetHevcGentle { get; init; } = "medium";

    public string SoftwarePresetHevcAggressive { get; init; } = "slow";

    public string SoftwarePresetAv1Gentle { get; init; } = "8";

    public string SoftwarePresetAv1Aggressive { get; init; } = "5";

    public string SoftwarePresetVp9Gentle { get; init; } = "3";

    public string SoftwarePresetVp9Aggressive { get; init; } = "1";
    public int HardwareQualityHevc { get; init; } = 24;

    public int HardwareQualityAv1 { get; init; } = 28;

    public int HardwareQualityVp9 { get; init; } = 28;

    public bool PreferAv1WhenAggressive { get; init; } = true;

    public bool PreferVp9ForWebm { get; init; } = true;

    public bool EnableTwoPassEncoding { get; init; } = true;

    public VideoEncoderPreset EncoderPreset { get; init; } = VideoEncoderPreset.Auto;

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

    public double FrameDecimationHighThreshold { get; init; } = 64;

    public double FrameDecimationLowThreshold { get; init; } = 15;

    public double FrameDecimationMaxDifference { get; init; } = 15;

    public bool EnableAudioNormalization { get; init; } = true;

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
            new[] { "CLOP_FFMPEG", "FFMPEG_EXECUTABLE" },
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
            new[] { "CLOP_FFPROBE", "FFPROBE_EXECUTABLE" },
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

        throw new FileNotFoundException($"Unable to locate required tool '{fileName}'. Run scripts/fetch-tools.ps1 and ensure the binaries remain under the 'tools' directory.");
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
            yield return Path.Combine(localAppData!, "Clop", "bin");
            yield return Path.Combine(localAppData!, "Clop", "bin", "x64");
        }

        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            yield return Path.Combine(roamingAppData!, "Clop", "bin");
            yield return Path.Combine(roamingAppData!, "Clop", "bin", "x64");
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