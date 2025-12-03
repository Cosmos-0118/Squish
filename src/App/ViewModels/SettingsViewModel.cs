using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ClopWindows.App.Infrastructure;
using ClopWindows.App.Localization;
using ClopWindows.App.Services;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private bool _enableFloatingResults;
    private bool _autoHideFloatingResults;
    private int _autoHideFloatingResultsAfter;
    private bool _enableClipboardOptimiser;
    private bool _autoCopyToClipboard;
    private bool _copyImageFilePath;
    private bool _optimiseClipboardFileDrops;
    private bool _optimiseVideoClipboard;
    private bool _optimisePdfClipboard;
    private bool _preserveDates;
    private bool _stripMetadata;
    private bool _enableAutomaticImageOptimisations;
    private bool _enableAutomaticVideoOptimisations;
    private bool _enableAutomaticPdfOptimisations;
    private bool _replaceOptimisedFilesInPlace;
    private bool _deleteOriginalAfterConversion;
    private bool _forceFullImageOptimisations;
    private bool _suppressStoreUpdates;
    private readonly Dictionary<ShortcutId, ShortcutPreferenceViewModel> _shortcutLookup;
    private readonly FloatingHudController _hudController;
    private readonly IFolderPicker _folderPicker;
    private ThemeOptionViewModel? _selectedThemeOption;
    private VideoEncoderPresetOptionViewModel? _selectedVideoEncoderPreset;
    private FloatingHudPlacement _floatingHudPlacement;
    private readonly string _gpuLabel;

    public IReadOnlyList<ShortcutPreferenceViewModel> AppShortcutPreferences { get; }
    public IReadOnlyList<ShortcutPreferenceViewModel> GlobalShortcutPreferences { get; }
    public IReadOnlyList<ThemeOptionViewModel> ThemeOptions { get; }
    public IReadOnlyList<VideoEncoderPresetOptionViewModel> VideoEncoderPresetOptions { get; }
    public string VideoPresetSubtitle { get; }
    public RelayCommand ResetHudLayoutCommand { get; }
    public IReadOnlyList<FloatingHudPlacementOptionViewModel> FloatingHudPlacementOptions { get; }
    public ObservableCollection<string> ImageDirectories { get; }
    public ObservableCollection<string> VideoDirectories { get; }
    public ObservableCollection<string> PdfDirectories { get; }
    public RelayCommand AddImageDirectoryCommand { get; }
    public RelayCommand AddVideoDirectoryCommand { get; }
    public RelayCommand AddPdfDirectoryCommand { get; }
    public RelayCommand RemoveImageDirectoryCommand { get; }
    public RelayCommand RemoveVideoDirectoryCommand { get; }
    public RelayCommand RemovePdfDirectoryCommand { get; }

    public SettingsViewModel(FloatingHudController hudController, IFolderPicker folderPicker)
    {
        _hudController = hudController ?? throw new ArgumentNullException(nameof(hudController));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        SettingsHost.EnsureInitialized();
        ShortcutCatalog.Initialize();
        ResetHudLayoutCommand = new RelayCommand(_ => ResetHudLayout(), _ => EnableFloatingResults);
        FloatingHudPlacementOptions = new ReadOnlyCollection<FloatingHudPlacementOptionViewModel>(CreatePlacementOptions());
        ThemeOptions = new ReadOnlyCollection<ThemeOptionViewModel>(CreateThemeOptions());
        _gpuLabel = DetermineGpuLabel();
        VideoEncoderPresetOptions = new ReadOnlyCollection<VideoEncoderPresetOptionViewModel>(CreateVideoEncoderPresetOptions(_gpuLabel));
        VideoPresetSubtitle = BuildVideoPresetSubtitle(_gpuLabel);
        ImageDirectories = new ObservableCollection<string>();
        VideoDirectories = new ObservableCollection<string>();
        PdfDirectories = new ObservableCollection<string>();
        AddImageDirectoryCommand = new RelayCommand(_ => AddDirectory(ImageDirectories, SettingsRegistry.ImageDirs));
        AddVideoDirectoryCommand = new RelayCommand(_ => AddDirectory(VideoDirectories, SettingsRegistry.VideoDirs));
        AddPdfDirectoryCommand = new RelayCommand(_ => AddDirectory(PdfDirectories, SettingsRegistry.PdfDirs));
        RemoveImageDirectoryCommand = new RelayCommand(path => RemoveDirectory(ImageDirectories, SettingsRegistry.ImageDirs, path));
        RemoveVideoDirectoryCommand = new RelayCommand(path => RemoveDirectory(VideoDirectories, SettingsRegistry.VideoDirs, path));
        RemovePdfDirectoryCommand = new RelayCommand(path => RemoveDirectory(PdfDirectories, SettingsRegistry.PdfDirs, path));
        Load();
        SettingsHost.SettingChanged += OnSettingChanged;
        ShortcutCatalog.ShortcutChanged += OnShortcutChanged;
        var descriptors = ShortcutCatalog.Descriptors;
        var appShortcuts = new List<ShortcutPreferenceViewModel>();
        var globalShortcuts = new List<ShortcutPreferenceViewModel>();
        _shortcutLookup = new Dictionary<ShortcutId, ShortcutPreferenceViewModel>();

        foreach (var descriptor in descriptors)
        {
            var vm = new ShortcutPreferenceViewModel(descriptor);
            _shortcutLookup[descriptor.Id] = vm;
            if (descriptor.Scope == ShortcutScope.MainWindow)
            {
                appShortcuts.Add(vm);
            }
            else
            {
                globalShortcuts.Add(vm);
            }
        }

        AppShortcutPreferences = new ReadOnlyCollection<ShortcutPreferenceViewModel>(appShortcuts);
        GlobalShortcutPreferences = new ReadOnlyCollection<ShortcutPreferenceViewModel>(globalShortcuts);
    }

    public ThemeOptionViewModel? SelectedThemeOption
    {
        get => _selectedThemeOption;
        set
        {
            if (SetProperty(ref _selectedThemeOption, value) && !_suppressStoreUpdates && value is not null)
            {
                SettingsHost.Set(SettingsRegistry.AppThemeMode, value.Mode);
            }
        }
    }

    public VideoEncoderPresetOptionViewModel? SelectedVideoEncoderPreset
    {
        get => _selectedVideoEncoderPreset;
        set
        {
            if (SetProperty(ref _selectedVideoEncoderPreset, value) && !_suppressStoreUpdates && value is not null)
            {
                SettingsHost.Set(SettingsRegistry.VideoEncoderPresetPreference, value.Preset);
            }
        }
    }

    public bool EnableFloatingResults
    {
        get => _enableFloatingResults;
        set
        {
            if (SetProperty(ref _enableFloatingResults, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.EnableFloatingResults, value);
            }
            ResetHudLayoutCommand.RaiseCanExecuteChanged();
        }
    }

    public bool AutoHideFloatingResults
    {
        get => _autoHideFloatingResults;
        set
        {
            if (SetProperty(ref _autoHideFloatingResults, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.AutoHideFloatingResults, value);
            }
        }
    }

    public int AutoHideFloatingResultsAfter
    {
        get => _autoHideFloatingResultsAfter;
        set
        {
            if (SetProperty(ref _autoHideFloatingResultsAfter, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.AutoHideFloatingResultsAfter, value);
            }
        }
    }

    public bool EnableClipboardOptimiser
    {
        get => _enableClipboardOptimiser;
        set
        {
            if (SetProperty(ref _enableClipboardOptimiser, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.EnableClipboardOptimiser, value);
            }
        }
    }

    public bool AutoCopyToClipboard
    {
        get => _autoCopyToClipboard;
        set
        {
            if (SetProperty(ref _autoCopyToClipboard, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.AutoCopyToClipboard, value);
            }
        }
    }

    public bool CopyImageFilePath
    {
        get => _copyImageFilePath;
        set
        {
            if (SetProperty(ref _copyImageFilePath, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.CopyImageFilePath, value);
            }
        }
    }

    public bool OptimiseVideoClipboard
    {
        get => _optimiseVideoClipboard;
        set
        {
            if (SetProperty(ref _optimiseVideoClipboard, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.OptimiseVideoClipboard, value);
            }
        }
    }

    public bool OptimiseClipboardFileDrops
    {
        get => _optimiseClipboardFileDrops;
        set
        {
            if (SetProperty(ref _optimiseClipboardFileDrops, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.OptimiseClipboardFileDrops, value);
            }
        }
    }

    public bool OptimisePdfClipboard
    {
        get => _optimisePdfClipboard;
        set
        {
            if (SetProperty(ref _optimisePdfClipboard, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.OptimisePdfClipboard, value);
            }
        }
    }

    public bool PreserveDates
    {
        get => _preserveDates;
        set
        {
            if (SetProperty(ref _preserveDates, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.PreserveDates, value);
            }
        }
    }

    public bool StripMetadata
    {
        get => _stripMetadata;
        set
        {
            if (SetProperty(ref _stripMetadata, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.StripMetadata, value);
            }
        }
    }

    public bool ReplaceOptimisedFilesInPlace
    {
        get => _replaceOptimisedFilesInPlace;
        set
        {
            if (SetProperty(ref _replaceOptimisedFilesInPlace, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.ReplaceOptimisedFilesInPlace, value);
            }

            if (!value && DeleteOriginalAfterConversion)
            {
                DeleteOriginalAfterConversion = false;
            }
        }
    }

    public bool DeleteOriginalAfterConversion
    {
        get => _deleteOriginalAfterConversion;
        set
        {
            var coercedValue = ReplaceOptimisedFilesInPlace ? value : false;
            if (SetProperty(ref _deleteOriginalAfterConversion, coercedValue) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.DeleteOriginalAfterConversion, coercedValue);
            }
        }
    }

    public bool EnableAutomaticImageOptimisations
    {
        get => _enableAutomaticImageOptimisations;
        set
        {
            if (SetProperty(ref _enableAutomaticImageOptimisations, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.EnableAutomaticImageOptimisations, value);
            }
        }
    }

    public bool EnableAutomaticVideoOptimisations
    {
        get => _enableAutomaticVideoOptimisations;
        set
        {
            if (SetProperty(ref _enableAutomaticVideoOptimisations, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.EnableAutomaticVideoOptimisations, value);
            }
        }
    }

    public bool EnableAutomaticPdfOptimisations
    {
        get => _enableAutomaticPdfOptimisations;
        set
        {
            if (SetProperty(ref _enableAutomaticPdfOptimisations, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.EnableAutomaticPdfOptimisations, value);
            }
        }
    }

    public bool ForceFullImageOptimisations
    {
        get => _forceFullImageOptimisations;
        set
        {
            if (SetProperty(ref _forceFullImageOptimisations, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.ForceFullImageOptimisations, value);
            }
        }
    }

    public FloatingHudPlacement FloatingHudPlacement
    {
        get => _floatingHudPlacement;
        set
        {
            if (SetProperty(ref _floatingHudPlacement, value))
            {
                if (!_suppressStoreUpdates)
                {
                    SettingsHost.Set(SettingsRegistry.FloatingHudPlacement, value);
                }

                OnPropertyChanged(nameof(FloatingHudPlacementStatus));
            }
        }
    }

    public string FloatingHudPlacementStatus => ClopStringCatalog.Get(GetPlacementResourceKey("placementStatus", FloatingHudPlacement));

    private void Load()
    {
        _suppressStoreUpdates = true;

        EnableFloatingResults = SettingsHost.Get(SettingsRegistry.EnableFloatingResults);
        AutoHideFloatingResults = SettingsHost.Get(SettingsRegistry.AutoHideFloatingResults);
        AutoHideFloatingResultsAfter = SettingsHost.Get(SettingsRegistry.AutoHideFloatingResultsAfter);
        FloatingHudPlacement = SettingsHost.Get(SettingsRegistry.FloatingHudPlacement);
        EnableClipboardOptimiser = SettingsHost.Get(SettingsRegistry.EnableClipboardOptimiser);
        AutoCopyToClipboard = SettingsHost.Get(SettingsRegistry.AutoCopyToClipboard);
        CopyImageFilePath = SettingsHost.Get(SettingsRegistry.CopyImageFilePath);
        OptimiseVideoClipboard = SettingsHost.Get(SettingsRegistry.OptimiseVideoClipboard);
        OptimiseClipboardFileDrops = SettingsHost.Get(SettingsRegistry.OptimiseClipboardFileDrops);
        OptimisePdfClipboard = SettingsHost.Get(SettingsRegistry.OptimisePdfClipboard);
        PreserveDates = SettingsHost.Get(SettingsRegistry.PreserveDates);
        StripMetadata = SettingsHost.Get(SettingsRegistry.StripMetadata);
        ReplaceOptimisedFilesInPlace = SettingsHost.Get(SettingsRegistry.ReplaceOptimisedFilesInPlace);
        DeleteOriginalAfterConversion = SettingsHost.Get(SettingsRegistry.DeleteOriginalAfterConversion);
        EnableAutomaticImageOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticImageOptimisations);
        EnableAutomaticVideoOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticVideoOptimisations);
        EnableAutomaticPdfOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticPdfOptimisations);
        ForceFullImageOptimisations = SettingsHost.Get(SettingsRegistry.ForceFullImageOptimisations);
        RefreshDirectoryCollections();
        var themeMode = SettingsHost.Get(SettingsRegistry.AppThemeMode);
        SelectedThemeOption = ThemeOptions.FirstOrDefault(option => option.Mode == themeMode) ?? ThemeOptions.FirstOrDefault();
        var encoderPreset = SettingsHost.Get(SettingsRegistry.VideoEncoderPresetPreference);
        SelectedVideoEncoderPreset = VideoEncoderPresetOptions.FirstOrDefault(option => option.Preset == encoderPreset) ?? VideoEncoderPresetOptions.FirstOrDefault();

        _suppressStoreUpdates = false;
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        _suppressStoreUpdates = true;
        switch (e.Name)
        {
            case var name when name == SettingsRegistry.EnableFloatingResults.Name:
                EnableFloatingResults = SettingsHost.Get(SettingsRegistry.EnableFloatingResults);
                break;
            case var name when name == SettingsRegistry.AutoHideFloatingResults.Name:
                AutoHideFloatingResults = SettingsHost.Get(SettingsRegistry.AutoHideFloatingResults);
                break;
            case var name when name == SettingsRegistry.AutoHideFloatingResultsAfter.Name:
                AutoHideFloatingResultsAfter = SettingsHost.Get(SettingsRegistry.AutoHideFloatingResultsAfter);
                break;
            case var name when name == SettingsRegistry.FloatingHudPlacement.Name:
                FloatingHudPlacement = SettingsHost.Get(SettingsRegistry.FloatingHudPlacement);
                break;
            case var name when name == SettingsRegistry.EnableClipboardOptimiser.Name:
                EnableClipboardOptimiser = SettingsHost.Get(SettingsRegistry.EnableClipboardOptimiser);
                break;
            case var name when name == SettingsRegistry.AutoCopyToClipboard.Name:
                AutoCopyToClipboard = SettingsHost.Get(SettingsRegistry.AutoCopyToClipboard);
                break;
            case var name when name == SettingsRegistry.CopyImageFilePath.Name:
                CopyImageFilePath = SettingsHost.Get(SettingsRegistry.CopyImageFilePath);
                break;
            case var name when name == SettingsRegistry.OptimiseVideoClipboard.Name:
                OptimiseVideoClipboard = SettingsHost.Get(SettingsRegistry.OptimiseVideoClipboard);
                break;
            case var name when name == SettingsRegistry.OptimiseClipboardFileDrops.Name:
                OptimiseClipboardFileDrops = SettingsHost.Get(SettingsRegistry.OptimiseClipboardFileDrops);
                break;
            case var name when name == SettingsRegistry.OptimisePdfClipboard.Name:
                OptimisePdfClipboard = SettingsHost.Get(SettingsRegistry.OptimisePdfClipboard);
                break;
            case var name when name == SettingsRegistry.PreserveDates.Name:
                PreserveDates = SettingsHost.Get(SettingsRegistry.PreserveDates);
                break;
            case var name when name == SettingsRegistry.StripMetadata.Name:
                StripMetadata = SettingsHost.Get(SettingsRegistry.StripMetadata);
                break;
            case var name when name == SettingsRegistry.ReplaceOptimisedFilesInPlace.Name:
                ReplaceOptimisedFilesInPlace = SettingsHost.Get(SettingsRegistry.ReplaceOptimisedFilesInPlace);
                break;
            case var name when name == SettingsRegistry.DeleteOriginalAfterConversion.Name:
                DeleteOriginalAfterConversion = SettingsHost.Get(SettingsRegistry.DeleteOriginalAfterConversion);
                break;
            case var name when name == SettingsRegistry.EnableAutomaticImageOptimisations.Name:
                EnableAutomaticImageOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticImageOptimisations);
                break;
            case var name when name == SettingsRegistry.EnableAutomaticVideoOptimisations.Name:
                EnableAutomaticVideoOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticVideoOptimisations);
                break;
            case var name when name == SettingsRegistry.EnableAutomaticPdfOptimisations.Name:
                EnableAutomaticPdfOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticPdfOptimisations);
                break;
            case var name when name == SettingsRegistry.ForceFullImageOptimisations.Name:
                ForceFullImageOptimisations = SettingsHost.Get(SettingsRegistry.ForceFullImageOptimisations);
                break;
            case var name when name == SettingsRegistry.ImageDirs.Name:
            case var name2 when name2 == SettingsRegistry.VideoDirs.Name:
            case var name3 when name3 == SettingsRegistry.PdfDirs.Name:
                RefreshDirectoryCollections();
                break;
            case var name when name == SettingsRegistry.AppThemeMode.Name:
                var themeMode = SettingsHost.Get(SettingsRegistry.AppThemeMode);
                SelectedThemeOption = ThemeOptions.FirstOrDefault(option => option.Mode == themeMode) ?? SelectedThemeOption;
                break;
            case var name when name == SettingsRegistry.VideoEncoderPresetPreference.Name:
                var preset = SettingsHost.Get(SettingsRegistry.VideoEncoderPresetPreference);
                SelectedVideoEncoderPreset = VideoEncoderPresetOptions.FirstOrDefault(option => option.Preset == preset) ?? SelectedVideoEncoderPreset;
                break;
        }
        _suppressStoreUpdates = false;
    }

    public void Dispose()
    {
        SettingsHost.SettingChanged -= OnSettingChanged;
        ShortcutCatalog.ShortcutChanged -= OnShortcutChanged;
    }

    private void ResetHudLayout()
    {
        _hudController.ResetHudLayout();
    }

    private void OnShortcutChanged(object? sender, ShortcutChangedEventArgs e)
    {
        if (_shortcutLookup.TryGetValue(e.Id, out var viewModel))
        {
            viewModel.Refresh();
        }
    }

    private void AddDirectory(ObservableCollection<string> target, SettingKey<string[]> key)
    {
        var initial = target.LastOrDefault();
        var selected = _folderPicker.PickFolder(initial, ClopStringCatalog.Get("settings.automation.pickFolder"));
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        if (target.Any(path => string.Equals(path, selected, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        target.Add(selected);
        PersistDirectorySetting(target, key);
    }

    private void RemoveDirectory(ObservableCollection<string> target, SettingKey<string[]> key, object? parameter)
    {
        if (parameter is not string path)
        {
            return;
        }

        var existing = target.FirstOrDefault(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        target.Remove(existing);
        PersistDirectorySetting(target, key);
    }

    private void RefreshDirectoryCollections()
    {
        ReplaceCollection(ImageDirectories, SettingsHost.Get(SettingsRegistry.ImageDirs));
        ReplaceCollection(VideoDirectories, SettingsHost.Get(SettingsRegistry.VideoDirs));
        ReplaceCollection(PdfDirectories, SettingsHost.Get(SettingsRegistry.PdfDirs));
    }

    private void ReplaceCollection(ObservableCollection<string> target, string[]? values)
    {
        target.Clear();
        if (values is null)
        {
            return;
        }

        foreach (var path in values)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                target.Add(path);
            }
        }
    }

    private void PersistDirectorySetting(ObservableCollection<string> target, SettingKey<string[]> key)
    {
        if (_suppressStoreUpdates)
        {
            return;
        }

        SettingsHost.Set(key, target.ToArray());
    }

    private static List<VideoEncoderPresetOptionViewModel> CreateVideoEncoderPresetOptions(string gpuLabel)
    {
        var label = string.IsNullOrWhiteSpace(gpuLabel) ? string.Empty : gpuLabel;
        return new List<VideoEncoderPresetOptionViewModel>
        {
            new(VideoEncoderPreset.Auto, ClopStringCatalog.Get("settings.optimisation.videoPreset.auto.title"), ClopStringCatalog.Get("settings.optimisation.videoPreset.auto.description")),
            new(VideoEncoderPreset.Cpu, ClopStringCatalog.Get("settings.optimisation.videoPreset.cpu.title"), ClopStringCatalog.Get("settings.optimisation.videoPreset.cpu.description")),
            new(VideoEncoderPreset.GpuQuality, ReplaceGpuLabel(ClopStringCatalog.Get("settings.optimisation.videoPreset.gpuQuality.title"), label), ClopStringCatalog.Get("settings.optimisation.videoPreset.gpuQuality.description")),
            new(VideoEncoderPreset.GpuSimple, ReplaceGpuLabel(ClopStringCatalog.Get("settings.optimisation.videoPreset.gpuSimple.title"), label), ClopStringCatalog.Get("settings.optimisation.videoPreset.gpuSimple.description")),
            new(VideoEncoderPreset.GpuCqp, ReplaceGpuLabel(ClopStringCatalog.Get("settings.optimisation.videoPreset.gpuCqp.title"), label), ClopStringCatalog.Get("settings.optimisation.videoPreset.gpuCqp.description"))
        };
    }

    private static string BuildVideoPresetSubtitle(string gpuLabel)
    {
        var subtitle = ClopStringCatalog.Get("settings.optimisation.videoPreset.subtitle");
        return ReplaceGpuLabel(subtitle, gpuLabel);
    }

    private static List<ThemeOptionViewModel> CreateThemeOptions()
    {
        return new List<ThemeOptionViewModel>
        {
            new(AppThemeMode.FollowSystem, ClopStringCatalog.Get("settings.theme.followSystem")),
            new(AppThemeMode.Light, ClopStringCatalog.Get("settings.theme.light")),
            new(AppThemeMode.Dark, ClopStringCatalog.Get("settings.theme.dark")),
            new(AppThemeMode.HighSaturation, ClopStringCatalog.Get("settings.theme.highSaturation"))
        };
    }

    private static List<FloatingHudPlacementOptionViewModel> CreatePlacementOptions()
    {
        return new List<FloatingHudPlacementOptionViewModel>
        {
            CreatePlacementOption(FloatingHudPlacement.TopLeft),
            CreatePlacementOption(FloatingHudPlacement.TopCenter),
            CreatePlacementOption(FloatingHudPlacement.TopRight),
            CreatePlacementOption(FloatingHudPlacement.MiddleLeft),
            CreatePlacementOption(FloatingHudPlacement.MiddleRight),
            CreatePlacementOption(FloatingHudPlacement.BottomLeft),
            CreatePlacementOption(FloatingHudPlacement.BottomCenter),
            CreatePlacementOption(FloatingHudPlacement.BottomRight)
        };
    }

    private static FloatingHudPlacementOptionViewModel CreatePlacementOption(FloatingHudPlacement placement)
    {
        var displayName = ClopStringCatalog.Get(GetPlacementResourceKey("placementOption", placement));
        return new FloatingHudPlacementOptionViewModel(placement, displayName);
    }

    private static string GetPlacementResourceKey(string prefix, FloatingHudPlacement placement)
    {
        var suffix = placement.ToString();
        if (string.IsNullOrEmpty(suffix))
        {
            return string.Empty;
        }

        var token = char.ToLowerInvariant(suffix[0]) + suffix[1..];
        return $"settings.floatingHud.{prefix}.{token}";
    }

    private static string DetermineGpuLabel()
    {
        try
        {
            var options = VideoOptimiserOptions.Default.WithHardwareOverride();
            var caps = options.HardwareOverride;
            if (caps is null)
            {
                return string.Empty;
            }

            if (caps.SupportsHevcNvenc || caps.SupportsNvenc)
            {
                return "NVIDIA";
            }

            if (caps.SupportsHevcAmf || caps.SupportsAmf)
            {
                return "AMD";
            }

            if (caps.SupportsHevcQsv || caps.SupportsQsv)
            {
                return "Intel";
            }
        }
        catch
        {
            // Hardware detection is best-effort; fall back to existing copy when probing fails.
        }

        return string.Empty;
    }

    private static string ReplaceGpuLabel(string source, string gpuLabel)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(gpuLabel))
        {
            return source;
        }

        return source.Replace("AMD", gpuLabel, StringComparison.OrdinalIgnoreCase);
    }
}
