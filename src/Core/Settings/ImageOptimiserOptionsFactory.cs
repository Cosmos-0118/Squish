using ClopWindows.Core.Optimizers;

namespace ClopWindows.Core.Settings;

public static class ImageOptimiserOptionsFactory
{
    public static ImageOptimiserOptions FromSettings()
    {
        SettingsHost.EnsureInitialized();
        var stripMetadata = SettingsHost.Get(SettingsRegistry.StripMetadata);
        var preserveColorProfiles = SettingsHost.Get(SettingsRegistry.PreserveColorMetadata);
        var forceFull = SettingsHost.Get(SettingsRegistry.ForceFullImageOptimisations);

        var metadataPolicy = MetadataPolicyOptions.Default with
        {
            PreserveMetadata = !stripMetadata,
            PreserveColorProfiles = preserveColorProfiles,
            StripGpsMetadata = stripMetadata
        };

        var fastPath = ImageOptimiserOptions.Default.WicFastPath with
        {
            Enabled = !forceFull,
            StripMetadata = stripMetadata
        };

        return ImageOptimiserOptions.Default with
        {
            MetadataPolicy = metadataPolicy,
            WicFastPath = fastPath
        };
    }
}
