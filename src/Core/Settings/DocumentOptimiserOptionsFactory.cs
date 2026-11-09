using Squish.Core.Documents;

namespace Squish.Core.Settings;

public static class DocumentOptimiserOptionsFactory
{
    public static DocumentOptimiserOptions FromSettings()
    {
        SettingsHost.EnsureInitialized();
        return DocumentOptimiserOptions.Default with
        {
            ImageOptions = ImageOptimiserOptionsFactory.FromSettings(),
            StripMetadata = SettingsHost.Get(SettingsRegistry.StripMetadata),
            PreserveDates = SettingsHost.Get(SettingsRegistry.PreserveDates)
        };
    }
}
