namespace ClopWindows.Core.Settings;

/// <summary>
/// User-facing encoder choices exposed in the app settings so Clop knows whether to prefer CPU or GPU paths.
/// </summary>
public enum VideoEncoderPreset
{
    /// <summary>
    /// Pick the best encoder automatically based on hardware capabilities and the requested container.
    /// </summary>
    Auto,

    /// <summary>
    /// Force software-only encoders (libx264/libx265) for maximum compatibility.
    /// </summary>
    Cpu,

    /// <summary>
    /// Use AMD HEVC QVBR with every quality switch enabled for the smallest files.
    /// </summary>
    GpuQuality,

    /// <summary>
    /// Use AMD HEVC QVBR with a minimal argument set for faster exports.
    /// </summary>
    GpuSimple,

    /// <summary>
    /// Use AMD HEVC CQP for CPU-like detail and the absolute smallest files.
    /// </summary>
    GpuCqp
}
