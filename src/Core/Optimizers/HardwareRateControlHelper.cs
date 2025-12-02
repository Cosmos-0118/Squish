using System;
using System.Collections.Generic;

namespace ClopWindows.Core.Optimizers;

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
