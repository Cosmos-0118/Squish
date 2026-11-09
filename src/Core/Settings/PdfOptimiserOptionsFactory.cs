using Squish.Core.Pdf;

namespace Squish.Core.Settings;

public static class PdfOptimiserOptionsFactory
{
    public static PdfOptimiserOptions FromSettings()
    {
        SettingsHost.EnsureInitialized();
        return PdfOptimiserOptions.Default with
        {
            ImageOptions = ImageOptimiserOptionsFactory.FromSettings(),
            StripMetadata = SettingsHost.Get(SettingsRegistry.StripMetadata),
            PreserveDates = SettingsHost.Get(SettingsRegistry.PreserveDates)
        };
    }
}
