using System;
using System.Collections.Generic;

namespace Squish.Core.Optimizers;

/// <summary>
/// Normalises hardware encoder quality values so NVENC/AMF/QSV stay roughly in line with
/// the CRF targets we use for software encoders. Hardware constant quality scales are
/// codec-specific, so we bias them slightly higher (smaller files) when the user does
/// not request aggressive quality. This keeps GPU outputs predictable without needing
/// format-specific tweaks elsewhere.
/// </summary>
internal static class HardwareRateControlHelper
{
    private static readonly IReadOnlyDictionary<VideoCodec, (int GentleBias, int AggressiveBias)> BiasTable =
        new Dictionary<VideoCodec, (int GentleBias, int AggressiveBias)>
        {
            [VideoCodec.H264] = (GentleBias: 3, AggressiveBias: 1),
            [VideoCodec.Hevc] = (GentleBias: 2, AggressiveBias: 1),
            [VideoCodec.Av1] = (GentleBias: 3, AggressiveBias: 2),
            [VideoCodec.Vp9] = (GentleBias: 3, AggressiveBias: 2)
        };

    public static int GetQuality(VideoCodec codec, int requestedQuality, VideoOptimiserOptions options, bool aggressive)
    {
        var softwareBaseline = codec switch
        {
            VideoCodec.Hevc => options.SoftwareCrfHevc,
            VideoCodec.Av1 => options.SoftwareCrfAv1,
            VideoCodec.Vp9 => options.SoftwareCrfVp9,
            _ => options.SoftwareCrf
        };

        var clampCandidate = Math.Max(requestedQuality, softwareBaseline);
        var bias = BiasTable.TryGetValue(codec, out var tuple)
            ? (aggressive ? tuple.AggressiveBias : tuple.GentleBias)
            : aggressive ? 1 : 2;

        var corrected = clampCandidate + bias;
        return Math.Clamp(corrected, 0, 51);
    }
}

/// <summary>
/// Constant-quality rate control for GPU encoders, so the perceptual CRF search can drive them like x265: the search
/// picks the quantiser, the encoder spends whatever bits the content needs. Without it the GPU path can only aim at a
/// blind fraction of the source bitrate. Encoders not listed here keep that bitrate path.
/// </summary>
internal static class HardwareConstantQuality
{
    /// <summary>Search window, score change per quantiser step and a starting point, by encoder.</summary>
    public static bool TryGetSearchRange(VideoEncoderSelection selection, out (int Min, int Max, double Slope, int Start) range)
    {
        range = selection.VideoEncoder switch
        {
            // Measured on 1080p camera and screen footage with hevc_amf: ~2.6 SSIMULACRA2 points per QP.
            "hevc_amf" or "h264_amf" => (16, 40, -2.6, 26),
            // AV1 quantisers are 0-255: ~0.25 points per step.
            "av1_amf" => (40, 200, -0.25, 110),
            "hevc_nvenc" or "h264_nvenc" or "hevc_qsv" or "h264_qsv" => (16, 40, -2.6, 26),
            "av1_nvenc" => (20, 55, -1.0, 34),
            _ => default
        };

        return range != default;
    }

    public static IReadOnlyList<string> Arguments(VideoEncoderSelection selection, int quality)
    {
        var q = quality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (selection.VideoEncoder.EndsWith("_amf", StringComparison.Ordinal))
        {
            // B-frames a notch coarser than P, as x264/x265 do; AV1 AMF has no B-frame quantiser.
            return selection.Codec == VideoCodec.Av1
                ? new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q }
                : new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", (quality + 2).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }

        if (selection.VideoEncoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            return new[] { "-rc", "vbr", "-cq", q, "-b:v", "0" };
        }

        return new[] { "-global_quality", q };
    }

    /// <summary>Drops rate-control flags from an encoder profile so they cannot override the constant-quality ones.</summary>
    public static IReadOnlyList<string> WithoutRateControl(IReadOnlyList<string> arguments)
    {
        var conflicting = new HashSet<string>(StringComparer.Ordinal) { "-rc", "-qp_i", "-qp_p", "-qp_b", "-cq", "-b:v", "-maxrate", "-bufsize", "-global_quality", "-qvbr_quality_level" };
        var result = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (conflicting.Contains(arguments[i]))
            {
                i++;
                continue;
            }

            result.Add(arguments[i]);
        }

        return result;
    }
}
