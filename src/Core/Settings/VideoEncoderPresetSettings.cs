using System.Collections.Generic;

namespace ClopWindows.Core.Settings;

/// <summary>
/// Captures the current video encoder preset preference and pushes it into optimisation metadata.
/// </summary>
public static class VideoEncoderPresetSettings
{
    public const string MetadataKey = "video.encoderPreset";

    public static VideoEncoderPresetSnapshot Capture()
    {
        var preset = SettingsHost.Get(SettingsRegistry.VideoEncoderPresetPreference);
        return new VideoEncoderPresetSnapshot(preset);
    }

    public static void ApplyTo(IDictionary<string, object?> metadata)
    {
        Capture().ApplyTo(metadata);
    }
}

public readonly record struct VideoEncoderPresetSnapshot(VideoEncoderPreset Preset)
{
    public void ApplyTo(IDictionary<string, object?> metadata)
    {
        metadata[VideoEncoderPresetSettings.MetadataKey] = Preset.ToString();
    }
}
