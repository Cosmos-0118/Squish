using System;
using System.Collections.Generic;
using System.Linq;
using Squish.Core.Optimizers;
using Squish.Core.Settings;
using Squish.Core.Shared;

namespace Squish.BackgroundService.Automation;

/// <summary>Immutable snapshot of everything the folder watcher reads from settings. Swapped atomically on change.</summary>
internal sealed record FolderWatchConfig(
    bool Paused,
    bool AutoImages,
    bool AutoVideos,
    IReadOnlyList<string> ImageDirectories,
    IReadOnlyList<string> VideoDirectories,
    IReadOnlySet<string> SkipImageFormats,
    IReadOnlySet<string> SkipVideoFormats,
    int MaxImageSizeMb,
    int MaxVideoSizeMb,
    int MaxImageJobs,
    int MaxVideoJobs,
    bool AutoPdfs = false,
    bool AutoDocuments = false,
    IReadOnlyList<string>? PdfDirectories = null,
    IReadOnlyList<string>? DocumentDirectories = null,
    int MaxDocumentSizeMb = 0,
    int MaxDocumentJobs = 0)
{
    public bool IsEnabled(ItemType type) => type switch
    {
        ItemType.Image => AutoImages,
        ItemType.Video => AutoVideos,
        ItemType.Pdf => AutoPdfs,
        ItemType.Document => AutoDocuments,
        _ => false
    };

    public bool IsFormatSkipped(ItemType type, string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var name = extension.TrimStart('.');
        return type switch
        {
            ItemType.Image => SkipImageFormats.Contains(name),
            ItemType.Video => SkipVideoFormats.Contains(name),
            _ => false
        };
    }

    public bool WithinSizeLimit(ItemType type, long bytes)
    {
        var sizeMb = bytes / (1024d * 1024d);
        return type switch
        {
            ItemType.Image => MaxImageSizeMb <= 0 || sizeMb <= MaxImageSizeMb,
            ItemType.Video => MaxVideoSizeMb <= 0 || sizeMb <= MaxVideoSizeMb,
            ItemType.Pdf or ItemType.Document => MaxDocumentSizeMb <= 0 || sizeMb <= MaxDocumentSizeMb,
            _ => false
        };
    }

    /// <summary>Maximum simultaneous watcher jobs for the type; zero means unlimited.</summary>
    public int JobLimit(ItemType type) => type switch
    {
        ItemType.Image => MaxImageJobs,
        ItemType.Video => MaxVideoJobs,
        ItemType.Pdf or ItemType.Document => MaxDocumentJobs,
        _ => 0
    };

    public IReadOnlyList<WatchRootSpec> BuildPlan() =>
        WatchPlan.Build(Paused, AutoImages, AutoVideos, ImageDirectories, VideoDirectories, AutoPdfs, AutoDocuments, PdfDirectories, DocumentDirectories);
}

/// <summary>Where the folder watcher gets its configuration from; swapped for a fake in tests and benchmarks.</summary>
internal interface IFolderWatchSettings
{
    FolderWatchConfig Current { get; }

    event EventHandler? Changed;

    /// <summary>Adds the per-request options (output behaviour, encoder presets, aggressiveness) to a new request.</summary>
    void ApplyRequestMetadata(ItemType type, FilePath path, IDictionary<string, object?> metadata);
}

internal sealed class SettingsFolderWatchSettings : IFolderWatchSettings, IDisposable
{
    private volatile FolderWatchConfig _current;

    public SettingsFolderWatchSettings()
    {
        SettingsHost.EnsureInitialized();
        _current = Load();
        SettingsHost.SettingChanged += OnSettingChanged;
    }

    public FolderWatchConfig Current => _current;

    public event EventHandler? Changed;

    public void ApplyRequestMetadata(ItemType type, FilePath path, IDictionary<string, object?> metadata)
    {
        OutputBehaviourSettings.ApplyTo(metadata);
        VideoEncoderPresetSettings.ApplyTo(metadata);
        AggressiveOptimisationHelper.Apply(type, path, metadata);
    }

    public void Dispose() => SettingsHost.SettingChanged -= OnSettingChanged;

    private static FolderWatchConfig Load() => new(
        Paused: SettingsHost.Get(SettingsRegistry.PauseAutomaticOptimisations),
        AutoImages: SettingsHost.Get(SettingsRegistry.EnableAutomaticImageOptimisations),
        AutoVideos: SettingsHost.Get(SettingsRegistry.EnableAutomaticVideoOptimisations),
        ImageDirectories: (SettingsHost.Get(SettingsRegistry.ImageDirs) ?? Array.Empty<string>()).ToArray(),
        VideoDirectories: (SettingsHost.Get(SettingsRegistry.VideoDirs) ?? Array.Empty<string>()).ToArray(),
        SkipImageFormats: new HashSet<string>(SettingsHost.Get(SettingsRegistry.ImageFormatsToSkip), StringComparer.OrdinalIgnoreCase),
        SkipVideoFormats: new HashSet<string>(SettingsHost.Get(SettingsRegistry.VideoFormatsToSkip), StringComparer.OrdinalIgnoreCase),
        MaxImageSizeMb: SettingsHost.Get(SettingsRegistry.MaxImageSizeMb),
        MaxVideoSizeMb: SettingsHost.Get(SettingsRegistry.MaxVideoSizeMb),
        MaxImageJobs: SettingsHost.Get(SettingsRegistry.MaxImageFileCount),
        MaxVideoJobs: SettingsHost.Get(SettingsRegistry.MaxVideoFileCount),
        AutoPdfs: SettingsHost.Get(SettingsRegistry.EnableAutomaticPdfOptimisations),
        AutoDocuments: SettingsHost.Get(SettingsRegistry.EnableAutomaticDocumentOptimisations),
        PdfDirectories: (SettingsHost.Get(SettingsRegistry.PdfDirs) ?? Array.Empty<string>()).ToArray(),
        DocumentDirectories: (SettingsHost.Get(SettingsRegistry.DocumentDirs) ?? Array.Empty<string>()).ToArray(),
        MaxDocumentSizeMb: SettingsHost.Get(SettingsRegistry.MaxDocumentSizeMb),
        MaxDocumentJobs: SettingsHost.Get(SettingsRegistry.MaxDocumentFileCount));

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!IsWatcherSetting(e.Name))
        {
            return;
        }

        _current = Load();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWatcherSetting(string name) =>
        string.Equals(name, SettingsRegistry.ImageDirs.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.VideoDirs.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.PdfDirs.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.DocumentDirs.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.EnableAutomaticPdfOptimisations.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.EnableAutomaticDocumentOptimisations.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.MaxDocumentSizeMb.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.MaxDocumentFileCount.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.EnableAutomaticImageOptimisations.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.EnableAutomaticVideoOptimisations.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.PauseAutomaticOptimisations.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.MaxImageSizeMb.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.MaxVideoSizeMb.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.MaxImageFileCount.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.MaxVideoFileCount.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.ImageFormatsToSkip.Name, StringComparison.Ordinal) ||
        string.Equals(name, SettingsRegistry.VideoFormatsToSkip.Name, StringComparison.Ordinal);
}
