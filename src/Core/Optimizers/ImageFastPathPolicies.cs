using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

internal static class ImageFastPathPolicies
{
    public static bool TryBuildEffectiveOptions(ImageOptimiserOptions options, out WicFastPathOptions fastPathOptions)
    {
        fastPathOptions = options.WicFastPath;

        if (!fastPathOptions.Enabled)
        {
            return false;
        }

        var policy = options.MetadataPolicy;

        // If metadata must be preserved but selectively adjusted (GPS removal or tag stripping),
        // the fast path cannot enforce that policy.
        var requiresSelectiveMetadata = policy.PreserveMetadata &&
                                        (policy.StripGpsMetadata || policy.AdditionalExifTagsToStrip.Count > 0);
        if (requiresSelectiveMetadata)
        {
            return false;
        }

        // When metadata should be stripped, ensure the fast path is configured to do so.
        if (!policy.PreserveMetadata)
        {
            if (!fastPathOptions.StripMetadata)
            {
                fastPathOptions = fastPathOptions with { StripMetadata = true };
            }
        }
        else if (fastPathOptions.StripMetadata)
        {
            // Caller wants to preserve metadata but fast path would remove it.
            return false;
        }

        return true;
    }

    public static bool RequiresPerceptualValidation(ImageOptimiserOptions options) => options.PerceptualGuard.Enabled;
}
