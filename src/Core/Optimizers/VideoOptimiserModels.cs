using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Squish.Core.Processes;
using Squish.Core.Shared;

namespace Squish.Core.Optimizers;

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
    public string? RateControlValueOverride { get; init; }

    /// <summary>
    /// Hardware only: run the encoder in its constant-quality mode with <see cref="Quality"/> as the quantiser (set
    /// when the perceptual search picked it) instead of a bitrate target.
    /// </summary>
    public bool ConstantQuality { get; init; }

    public static VideoEncoderSelection Software(VideoCodec codec, string encoder, string container, string pixelFormat, int quality, IReadOnlyList<string>? additionalArgs = null, bool supportsTwoPass = true, bool suggestTwoPass = true, int suggestedLookahead = 24, bool sceneCutAware = true, string rateControlFlag = "-crf", string? rateControlValueOverride = null)
        => new(codec, container, encoder, pixelFormat, false, null, null, quality, null, additionalArgs ?? Array.Empty<string>(), supportsTwoPass, suggestTwoPass, suggestedLookahead, sceneCutAware, rateControlFlag)
        {
            RateControlValueOverride = rateControlValueOverride
        };

    public static VideoEncoderSelection Hardware(VideoCodec codec, string encoder, string container, string pixelFormat, string acceleration, string? outputFormat, int quality, IReadOnlyList<string>? additionalArgs, bool supportsTwoPass, int suggestedLookahead, bool sceneCutAware, string rateControlFlag = "-cq", string? rateControlValueOverride = null)
        => new(codec, container, encoder, pixelFormat, true, acceleration, outputFormat, quality, null, additionalArgs ?? Array.Empty<string>(), supportsTwoPass, false, suggestedLookahead, sceneCutAware, rateControlFlag)
        {
            RateControlValueOverride = rateControlValueOverride
        };
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

/// <summary>
/// Which subtitle streams survive into the output and how they are written. Containers differ: Matroska keeps
/// everything as-is, MP4/MOV only take <c>mov_text</c> (so text subtitles are converted and bitmap ones cannot be kept).
/// </summary>
public sealed record SubtitlePlan(IReadOnlyList<int> Streams, string? Codec)
{
    public static SubtitlePlan None { get; } = new(Array.Empty<int>(), null);

    public bool Any => Streams.Count > 0 && Codec is not null;

    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text"
    };

    public static SubtitlePlan For(IReadOnlyList<string> sourceCodecs, string outputExtension)
    {
        if (sourceCodecs.Count == 0)
        {
            return None;
        }

        switch (outputExtension.Trim().TrimStart('.').ToLowerInvariant())
        {
            case "mp4":
            case "m4v":
            case "mov":
                return Select(sourceCodecs, codec => TextCodecs.Contains(codec), "mov_text");
            case "mkv":
                return Select(sourceCodecs, _ => true, "copy");
            case "webm":
                return Select(sourceCodecs, codec => codec.Equals("webvtt", StringComparison.OrdinalIgnoreCase), "copy");
            default:
                return None;
        }
    }

    private static SubtitlePlan Select(IReadOnlyList<string> codecs, Func<string, bool> keep, string codec)
    {
        var streams = new List<int>();
        for (var i = 0; i < codecs.Count; i++)
        {
            if (keep(codecs[i]))
            {
                streams.Add(i);
            }
        }

        return streams.Count == 0 ? None : new SubtitlePlan(streams, codec);
    }
}

public sealed record AudioPlan(bool RemoveAudio, bool CopyStream, string? Encoder, int? BitrateKbps, int? Channels, bool NormalizeLoudness, LoudnessProfile Loudness)
{
    /// <summary>Playback speed the video is retimed by; the audio is run through <c>atempo</c> by the same factor.</summary>
    public double? TempoFactor { get; init; }

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

    /// <summary>Codec names of the subtitle streams, in stream order (so index N is ffmpeg's <c>0:s:N</c>).</summary>
    public IReadOnlyList<string> SubtitleCodecs { get; init; } = Array.Empty<string>();
}

public sealed record VideoStreamInfo(
    string? CodecName,
    string? Profile,
    string? PixelFormat,
    string? ColorSpace,
    string? ColorTransfer,
    string? ColorPrimaries,
    string? ColorRange,
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

/// <summary>
/// How capable the machine's best GPU video encoder is, from the encoders that actually opened (see
/// <see cref="VideoHardwareDetector"/>). It sets how large a job must be before the GPU beats software SVT-AV1 for
/// the automatic choice: newer silicon compresses nearly as well as software at several times the speed.
/// </summary>
public enum GpuEncoderTier
{
    None = 0,

    /// <summary>H.264 only (or nothing worth using).</summary>
    Legacy = 1,

    /// <summary>Hardware HEVC, no AV1: RTX 20/30, GTX 16, Intel 11th-14th gen, RDNA 2. Files run ~25-40% larger than SVT-AV1.</summary>
    Modern = 2,

    /// <summary>First-generation hardware AV1: RTX 40, Arc Alchemist/Meteor Lake, RDNA 3/4. ~12-22% larger than SVT-AV1.</summary>
    Av1 = 3,

    /// <summary>Latest AV1 silicon: RTX 50, Arc Battlemage, Lunar/Arrow Lake. ~6-11% larger than SVT-AV1.</summary>
    Flagship = 4
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
    bool SupportsHevcQsv,
    bool SupportsSoftwareAv1 = true)
{
    public static VideoHardwareCapabilities CpuOnly { get; } = new(false, false, false, false, false, false, false, false, false, false);

    // H.264 / VP9 are tracked per encoder so a vendor that only does HEVC+AV1 is never asked for H.264 (and vice versa).
    // Hand-built capabilities that leave these unset fall back to the vendor-wide flag, as before.
    public bool? SupportsH264Nvenc { get; init; }

    public bool? SupportsH264Amf { get; init; }

    public bool? SupportsH264Qsv { get; init; }

    public bool? SupportsVp9Qsv { get; init; }

    /// <summary>The newest AV1 silicon generation, confirmed by a working AV1 encode plus the adapter name.</summary>
    public bool FlagshipAv1Silicon { get; init; }

    public bool CanEncodeH264Nvenc => SupportsH264Nvenc ?? SupportsNvenc;

    public bool CanEncodeH264Amf => SupportsH264Amf ?? SupportsAmf;

    public bool CanEncodeH264Qsv => SupportsH264Qsv ?? SupportsQsv;

    public bool CanEncodeVp9Qsv => SupportsVp9Qsv ?? SupportsQsv;

    public bool HasAnyHardware => SupportsNvenc || SupportsAmf || SupportsQsv || SupportsDxva;

    public bool SupportsAv1Hardware => SupportsAv1Nvenc || SupportsAv1Amf || SupportsAv1Qsv;

    public bool SupportsHevcHardware => SupportsHevcNvenc || SupportsHevcAmf || SupportsHevcQsv;

    public GpuEncoderTier EncoderTier
        => SupportsAv1Hardware ? (FlagshipAv1Silicon ? GpuEncoderTier.Flagship : GpuEncoderTier.Av1)
            : SupportsHevcHardware ? GpuEncoderTier.Modern
            : SupportsNvenc || SupportsAmf || SupportsQsv ? GpuEncoderTier.Legacy
            : GpuEncoderTier.None;
}
