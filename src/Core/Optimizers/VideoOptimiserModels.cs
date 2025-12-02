using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ClopWindows.Core.Processes;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

/// <summary>
/// Represents the final encoder settings chosen for a video optimisation run.
/// </summary>
public sealed record VideoEncoderSelection(
    VideoCodec Codec,
    string ContainerExtension,
    string VideoEncoder,
    string PixelFormat,
    bool UseHardwareEncoder,
    string? HardwareAcceleration,
    string? HardwareOutputFormat,
    int Quality,
    int? TargetBitrateKbps,
    IReadOnlyList<string> AdditionalArguments,
    bool SupportsTwoPass,
    bool SuggestTwoPass,
    int SuggestedLookaheadFrames,
    bool SceneCutAware,
    string RateControlFlag)
{
    public static VideoEncoderSelection Software(VideoCodec codec, string encoder, string container, string pixelFormat, int quality, IReadOnlyList<string>? additionalArgs = null, bool supportsTwoPass = true, bool suggestTwoPass = true, int suggestedLookahead = 24, bool sceneCutAware = true, string rateControlFlag = "-crf")
        => new(codec, container, encoder, pixelFormat, false, null, null, quality, null, additionalArgs ?? Array.Empty<string>(), supportsTwoPass, suggestTwoPass, suggestedLookahead, sceneCutAware, rateControlFlag);

    public static VideoEncoderSelection Hardware(VideoCodec codec, string encoder, string container, string pixelFormat, string acceleration, string? outputFormat, int quality, IReadOnlyList<string>? additionalArgs, bool supportsTwoPass, int suggestedLookahead, bool sceneCutAware, string rateControlFlag = "-cq")
        => new(codec, container, encoder, pixelFormat, true, acceleration, outputFormat, quality, null, additionalArgs ?? Array.Empty<string>(), supportsTwoPass, false, suggestedLookahead, sceneCutAware, rateControlFlag);
}

public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
    Vp9
}

public sealed record FrameDecimationPlan(bool Enabled, double HighThreshold, double LowThreshold, double MaxDurationDifference)
{
    public static FrameDecimationPlan Disabled { get; } = new(false, 0.0, 0.0, 0.0);
}

public sealed record LoudnessProfile(double Integrated, double TruePeak, double LoudnessRange);

public sealed record AudioPlan(bool RemoveAudio, bool CopyStream, string? Encoder, int? BitrateKbps, int? Channels, bool NormalizeLoudness, LoudnessProfile Loudness)
{
    public static AudioPlan Remove => new(true, false, null, null, null, false, new LoudnessProfile(-24, -2, 7));
    public static AudioPlan Copy => new(false, true, null, null, null, false, new LoudnessProfile(-24, -2, 7));
}

public sealed record VideoProbeInfo(
    string? FormatName,
    string? FormatLongName,
    double? DurationSeconds,
    long? ContainerBitrate,
    long? ContainerSize,
    VideoStreamInfo? Video,
    AudioStreamInfo? Audio)
{
    public string NormalizedVideoCodec => CodecVocabulary.NormalizeVideo(Video?.CodecName);
    public string NormalizedAudioCodec => CodecVocabulary.NormalizeAudio(Audio?.CodecName);
}

public sealed record VideoStreamInfo(
    string? CodecName,
    string? Profile,
    string? PixelFormat,
    string? ColorSpace,
    int? Width,
    int? Height,
    long? Bitrate,
    double? FrameRate,
    bool IsHdr,
    bool IsInterlaced);

public sealed record AudioStreamInfo(
    string? CodecName,
    string? Profile,
    int? Channels,
    int? SampleRate,
    long? Bitrate);

public sealed record RemuxPlan(bool Enabled, RemuxReason Reason)
{
    public static RemuxPlan Disabled { get; } = new(false, RemuxReason.None);
}

public enum RemuxReason
{
    None,
    ContainerNormalisation,
    MinimalSavings
}

internal static class CodecVocabulary
{
    public static string NormalizeVideo(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return string.Empty;
        }

        var value = codec.Trim().ToLowerInvariant();
        return value switch
        {
            "h264" or "avc1" or "avc" => "h264",
            "h265" or "hevc" => "hevc",
            "av1" or "av01" => "av1",
            "vp9" or "vp09" => "vp9",
            "vp8" or "vp08" => "vp8",
            var name when name.Contains("prores", StringComparison.OrdinalIgnoreCase) => "prores",
            var name when name.Contains("dnx", StringComparison.OrdinalIgnoreCase) => "dnx",
            _ => value
        };
    }

    public static string NormalizeVideo(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264",
        VideoCodec.Hevc => "hevc",
        VideoCodec.Av1 => "av1",
        VideoCodec.Vp9 => "vp9",
        _ => "h264"
    };

    public static string NormalizeAudio(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return string.Empty;
        }

        var value = codec.Trim().ToLowerInvariant();
        return value switch
        {
            "aac" or "mp4a" or "aac_latm" => "aac",
            "opus" => "opus",
            "vorbis" => "vorbis",
            _ => value
        };
    }
}

public enum AnimatedExportFormat
{
    Gif,
    Apng,
    AnimatedWebp
}

public sealed record VideoHardwareCapabilities(
    bool SupportsNvenc,
    bool SupportsAmf,
    bool SupportsQsv,
    bool SupportsDxva,
    bool SupportsAv1Nvenc,
    bool SupportsAv1Amf,
    bool SupportsAv1Qsv,
    bool SupportsHevcNvenc,
    bool SupportsHevcAmf,
    bool SupportsHevcQsv)
{
    public static VideoHardwareCapabilities CpuOnly { get; } = new(false, false, false, false, false, false, false, false, false, false);

    public bool HasAnyHardware => SupportsNvenc || SupportsAmf || SupportsQsv || SupportsDxva;

    public bool SupportsAv1Hardware => SupportsAv1Nvenc || SupportsAv1Amf || SupportsAv1Qsv;

    public bool SupportsHevcHardware => SupportsHevcNvenc || SupportsHevcAmf || SupportsHevcQsv;
}

public static class VideoHardwareDetector
{
    private static readonly object Gate = new();
    private static VideoHardwareCapabilities? _cached;
    private static string? _cachedKey;

    public static VideoHardwareCapabilities Detect(string ffmpegPath, bool probe)
    {
        if (!probe)
        {
            return VideoHardwareCapabilities.CpuOnly;
        }

        var cacheKey = BuildCacheKey(ffmpegPath);

        lock (Gate)
        {
            if (_cached is not null && string.Equals(_cachedKey, cacheKey, StringComparison.Ordinal))
            {
                return _cached;
            }

            try
            {
                var encoderResult = ProcessRunner.Run(ffmpegPath, new[] { "-hide_banner", "-encoders" }, ProcessRunnerOptions.Create(throwOnError: false));
                var encoders = encoderResult.ExitCode == 0
                    ? ParseLines(encoderResult.StandardOutput)
                    : Array.Empty<string>();

                var hwAccelResult = ProcessRunner.Run(ffmpegPath, new[] { "-hide_banner", "-hwaccels" }, ProcessRunnerOptions.Create(throwOnError: false));
                var hwAccels = hwAccelResult.ExitCode == 0
                    ? ParseLines(hwAccelResult.StandardOutput)
                    : Array.Empty<string>();

                var hasNvDriver = HasVendorLibrary("nvcuda.dll") || HasVendorLibrary("nvEncodeAPI64.dll");
                var hasAmfDriver = HasVendorLibrary("amfrt64.dll") || HasVendorLibrary("amfrt32.dll");
                var hasQsvDriver = HasVendorLibrary("libmfxhw64.dll") || HasVendorLibrary("libmfxhw32.dll");

                var caps = new VideoHardwareCapabilities(
                    SupportsNvenc: hasNvDriver && encoders.Any(line => line.Contains("nvenc", StringComparison.OrdinalIgnoreCase)),
                    SupportsAmf: hasAmfDriver && encoders.Any(line => line.Contains("_amf", StringComparison.OrdinalIgnoreCase)),
                    SupportsQsv: hasQsvDriver && encoders.Any(line => line.Contains("_qsv", StringComparison.OrdinalIgnoreCase)),
                    SupportsDxva: hwAccels.Any(line => line.Contains("d3d11", StringComparison.OrdinalIgnoreCase) || line.Contains("dxva2", StringComparison.OrdinalIgnoreCase)),
                    SupportsAv1Nvenc: hasNvDriver && encoders.Any(line => line.Contains("av1_nvenc", StringComparison.OrdinalIgnoreCase)),
                    SupportsAv1Amf: hasAmfDriver && encoders.Any(line => line.Contains("av1_amf", StringComparison.OrdinalIgnoreCase)),
                    SupportsAv1Qsv: hasQsvDriver && encoders.Any(line => line.Contains("av1_qsv", StringComparison.OrdinalIgnoreCase)),
                    SupportsHevcNvenc: hasNvDriver && encoders.Any(line => line.Contains("hevc_nvenc", StringComparison.OrdinalIgnoreCase)),
                    SupportsHevcAmf: hasAmfDriver && encoders.Any(line => line.Contains("hevc_amf", StringComparison.OrdinalIgnoreCase)),
                    SupportsHevcQsv: hasQsvDriver && encoders.Any(line => line.Contains("hevc_qsv", StringComparison.OrdinalIgnoreCase)));

                _cached = caps;
                _cachedKey = cacheKey;
                return caps;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to probe ffmpeg hardware capabilities: {ex.Message}");
                _cached = VideoHardwareCapabilities.CpuOnly;
                _cachedKey = cacheKey;
                return _cached;
            }
        }
    }

    private static string BuildCacheKey(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return string.Empty;
        }

        try
        {
            var info = new FileInfo(ffmpegPath);
            var timestamp = info.Exists ? info.LastWriteTimeUtc.Ticks : 0L;
            return $"{ffmpegPath}|{timestamp}";
        }
        catch
        {
            return ffmpegPath;
        }
    }

    private static IReadOnlyCollection<string> ParseLines(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return Array.Empty<string>();
        }

        var lines = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        return new ReadOnlyCollection<string>(lines);
    }

    private static bool HasVendorLibrary(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return false;
        }

        try
        {
            if (NativeLibrary.TryLoad(libraryName, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch
        {
            // ignored — absence means the driver is missing.
        }

        return false;
    }
}
