using Squish.Core.Optimizers;

namespace Squish.Core.Settings;

/// <summary>
/// Builds <see cref="VideoOptimiserOptions"/> from the user's saved settings, the video counterpart of
/// <see cref="ImageOptimiserOptionsFactory"/>. Without it the metadata, date and colour-profile toggles in Settings
/// (and the metadata hotkey) only ever affected images.
/// </summary>
public static class VideoOptimiserOptionsFactory
{
    public static VideoOptimiserOptions FromSettings() => Apply(VideoOptimiserOptions.Default);

    /// <summary>Overlays the current settings on <paramref name="baseline"/>, keeping everything else (paths, hardware override, ...).</summary>
    public static VideoOptimiserOptions Apply(VideoOptimiserOptions baseline)
    {
        SettingsHost.EnsureInitialized();
        return Apply(baseline, SettingsHost.Store);
    }

    public static VideoOptimiserOptions Apply(VideoOptimiserOptions baseline, SettingsStore store)
    {
        return baseline with
        {
            StripMetadata = store.Get(SettingsRegistry.StripMetadata),
            PreserveTimestamps = store.Get(SettingsRegistry.PreserveDates),
            PreserveColorMetadata = store.Get(SettingsRegistry.PreserveColorMetadata),
            CapFps = store.Get(SettingsRegistry.CapVideoFps),
            TargetFps = (int)System.Math.Round(store.Get(SettingsRegistry.TargetVideoFps)),
            MinFps = (int)System.Math.Round(store.Get(SettingsRegistry.MinVideoFps)),
            RemoveAudio = store.Get(SettingsRegistry.RemoveAudioFromVideos)
        };
    }
}
