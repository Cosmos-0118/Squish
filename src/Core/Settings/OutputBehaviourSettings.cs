using System.Collections.Generic;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Settings;

public static class OutputBehaviourSettings
{
    public static OutputBehaviourSnapshot Capture()
    {
        var replaceInPlace = SettingsHost.Get(SettingsRegistry.ReplaceOptimisedFilesInPlace);
        var deleteAfterConversion = replaceInPlace && SettingsHost.Get(SettingsRegistry.DeleteOriginalAfterConversion);
        var forceFullImages = SettingsHost.Get(SettingsRegistry.ForceFullImageOptimisations);
        return new OutputBehaviourSnapshot(replaceInPlace, deleteAfterConversion, forceFullImages);
    }

    public static void ApplyTo(IDictionary<string, object?> metadata)
    {
        Capture().ApplyTo(metadata);
    }
}

public readonly record struct OutputBehaviourSnapshot(bool ReplaceInPlace, bool DeleteConvertedSource, bool ForceFullImageOptimisations)
{
    public void ApplyTo(IDictionary<string, object?> metadata)
    {
        if (ForceFullImageOptimisations)
        {
            metadata[OptimisationMetadata.ImageForceFullOptimisation] = true;
        }

        if (!ReplaceInPlace)
        {
            return;
        }

        metadata[OptimisationMetadata.OutputReplaceOriginal] = true;
        if (DeleteConvertedSource)
        {
            metadata[OptimisationMetadata.OutputDeleteConvertedSource] = true;
        }
    }
}
