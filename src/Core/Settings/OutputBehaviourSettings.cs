using System.Collections.Generic;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Settings;

public static class OutputBehaviourSettings
{
    public static OutputBehaviourSnapshot Capture()
    {
        var replaceInPlace = SettingsHost.Get(SettingsRegistry.ReplaceOptimisedFilesInPlace);
        var deleteAfterConversion = replaceInPlace && SettingsHost.Get(SettingsRegistry.DeleteOriginalAfterConversion);
        return new OutputBehaviourSnapshot(replaceInPlace, deleteAfterConversion);
    }

    public static void ApplyTo(IDictionary<string, object?> metadata)
    {
        Capture().ApplyTo(metadata);
    }
}

public readonly record struct OutputBehaviourSnapshot(bool ReplaceInPlace, bool DeleteConvertedSource)
{
    public void ApplyTo(IDictionary<string, object?> metadata)
    {
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
