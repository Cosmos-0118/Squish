using ClopWindows.Core.Optimizers;

namespace ClopWindows.Core.Settings;

public static class ImageOptimiserOptionsFactory
{
    public static ImageOptimiserOptions FromSettings()
    {
        SettingsHost.EnsureInitialized();
        var stripMetadata = SettingsHost.Get(SettingsRegistry.StripMetadata);
        var preserveColorProfiles = SettingsHost.Get(SettingsRegistry.PreserveColorMetadata);

        var metadataPolicy = MetadataPolicyOptions.Default with
        {
            PreserveMetadata = !stripMetadata,
            PreserveColorProfiles = preserveColorProfiles,
            StripGpsMetadata = stripMetadata
        };

        var defaults = ImageOptimiserOptions.Default;

        return defaults with
        {
            MetadataPolicy = metadataPolicy,
            DownscaleRetina = SettingsHost.Get(SettingsRegistry.DownscaleRetinaImages),
            FormatsToConvertToJpeg = Normalise(SettingsHost.Get(SettingsRegistry.FormatsToConvertToJpeg), "heic", "heif"),
            FormatsToConvertToPng = Normalise(SettingsHost.Get(SettingsRegistry.FormatsToConvertToPng)),
            AdvancedCodecs = defaults.AdvancedCodecs with
            {
                AllowWebpOutput = SettingsHost.Get(SettingsRegistry.AllowWebpImageOutput),
                AllowAvifOutput = SettingsHost.Get(SettingsRegistry.AllowAvifImageOutput)
            }
        };
    }

    private static HashSet<string> Normalise(IEnumerable<string>? formats, params string[] alwaysInclude)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (formats is not null)
        {
            foreach (var format in formats)
            {
                var value = format?.Trim().TrimStart('.');
                if (!string.IsNullOrEmpty(value))
                {
                    set.Add(value);
                    if (value.Equals("tiff", StringComparison.OrdinalIgnoreCase))
                    {
                        set.Add("tif");
                    }
                    else if (value.Equals("tif", StringComparison.OrdinalIgnoreCase))
                    {
                        set.Add("tiff");
                    }
                    else if (value.Equals("jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        set.Add("jpg");
                    }
                }
            }
        }

        // HEIC can never be written back, so it is always converted when selected for JPEG.
        if (alwaysInclude.Length > 0 && set.Contains(alwaysInclude[0]))
        {
            foreach (var extra in alwaysInclude)
            {
                set.Add(extra);
            }
        }

        return set;
    }
}
