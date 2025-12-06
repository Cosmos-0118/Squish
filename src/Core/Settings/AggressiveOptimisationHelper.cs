using System;
using System.Collections.Generic;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Settings;

public static class AggressiveOptimisationHelper
{
    public static void Apply(ItemType itemType, FilePath sourcePath, IDictionary<string, object?> metadata, bool? overrideValue = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        SettingsHost.EnsureInitialized();

        switch (itemType)
        {
            case ItemType.Image:
                ApplyImagePreference(sourcePath, metadata, overrideValue);
                break;
            case ItemType.Video:
                ApplyFlag(metadata, OptimisationMetadata.VideoAggressive, overrideValue ?? SettingsHost.Get(SettingsRegistry.UseAggressiveOptimisationMp4));
                break;
            case ItemType.Pdf:
            case ItemType.Document:
                ApplyFlag(metadata, OptimisationMetadata.PdfAggressive, overrideValue ?? SettingsHost.Get(SettingsRegistry.UseAggressiveOptimisationPdf));
                break;
        }
    }

    private static void ApplyImagePreference(FilePath sourcePath, IDictionary<string, object?> metadata, bool? overrideValue)
    {
        var requested = overrideValue ?? EvaluateImagePreference(sourcePath);
        ApplyFlag(metadata, OptimisationMetadata.ImageAggressive, requested);
    }

    private static bool EvaluateImagePreference(FilePath sourcePath)
    {
        var extension = sourcePath.Extension?.TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => SettingsHost.Get(SettingsRegistry.UseAggressiveOptimisationJpeg),
            "png" => SettingsHost.Get(SettingsRegistry.UseAggressiveOptimisationPng),
            "gif" => SettingsHost.Get(SettingsRegistry.UseAggressiveOptimisationGif),
            _ => false
        };
    }

    private static void ApplyFlag(IDictionary<string, object?> metadata, string key, bool requested)
    {
        if (requested)
        {
            if (!metadata.ContainsKey(key))
            {
                metadata[key] = true;
            }
        }
        else
        {
            metadata.Remove(key);
        }
    }
}
