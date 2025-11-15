using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ClopWindows.App.Infrastructure;
using ClopWindows.App.Localization;
using ClopWindows.App.Services;
using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private bool _enableFloatingResults;
    private bool _autoHideFloatingResults;
    private int _autoHideFloatingResultsAfter;
    private bool _enableClipboardOptimiser;
    private bool _autoCopyToClipboard;
    private bool _optimiseVideoClipboard;
    private bool _optimisePdfClipboard;
    private bool _preserveDates;
    private bool _stripMetadata;
    private bool _enableAutomaticImageOptimisations;
    private bool _enableAutomaticVideoOptimisations;
    private bool _enableAutomaticPdfOptimisations;
    private bool _suppressStoreUpdates;
    private readonly Dictionary<ShortcutId, ShortcutPreferenceViewModel> _shortcutLookup;
    private readonly FloatingHudController _hudController;
    private ThemeOptionViewModel? _selectedThemeOption;
    private bool _floatingHudPinned;

    public IReadOnlyList<ShortcutPreferenceViewModel> AppShortcutPreferences { get; }
    public IReadOnlyList<ShortcutPreferenceViewModel> GlobalShortcutPreferences { get; }
    public IReadOnlyList<ThemeOptionViewModel> ThemeOptions { get; }
    public RelayCommand BeginHudPlacementCommand { get; }
    public RelayCommand ClearHudPlacementCommand { get; }

    public SettingsViewModel(FloatingHudController hudController)
    {
        _hudController = hudController ?? throw new ArgumentNullException(nameof(hudController));
        SettingsHost.EnsureInitialized();
        ShortcutCatalog.Initialize();
        BeginHudPlacementCommand = new RelayCommand(_ => BeginHudPlacement(), _ => EnableFloatingResults);
        ClearHudPlacementCommand = new RelayCommand(_ => ClearHudPlacement(), _ => EnableFloatingResults && FloatingHudPinned);
        ThemeOptions = new ReadOnlyCollection<ThemeOptionViewModel>(CreateThemeOptions());
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

    public bool EnableFloatingResults
    {
        get => _enableFloatingResults;
        set
        {
            if (SetProperty(ref _enableFloatingResults, value) && !_suppressStoreUpdates)
            {
                SettingsHost.Set(SettingsRegistry.EnableFloatingResults, value);
            }
            BeginHudPlacementCommand.RaiseCanExecuteChanged();
            ClearHudPlacementCommand.RaiseCanExecuteChanged();
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

    public bool FloatingHudPinned
    {
        get => _floatingHudPinned;
        private set
        {
            if (SetProperty(ref _floatingHudPinned, value))
            {
                OnPropertyChanged(nameof(FloatingHudPlacementStatus));
                ClearHudPlacementCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FloatingHudPlacementStatus => FloatingHudPinned
        ? ClopStringCatalog.Get("settings.floatingHud.pinStatusPinned")
        : ClopStringCatalog.Get("settings.floatingHud.pinStatusUnpinned");

    private void Load()
    {
        _suppressStoreUpdates = true;

        EnableFloatingResults = SettingsHost.Get(SettingsRegistry.EnableFloatingResults);
        AutoHideFloatingResults = SettingsHost.Get(SettingsRegistry.AutoHideFloatingResults);
        AutoHideFloatingResultsAfter = SettingsHost.Get(SettingsRegistry.AutoHideFloatingResultsAfter);
        FloatingHudPinned = SettingsHost.Get(SettingsRegistry.FloatingHudPinned);
        EnableClipboardOptimiser = SettingsHost.Get(SettingsRegistry.EnableClipboardOptimiser);
        AutoCopyToClipboard = SettingsHost.Get(SettingsRegistry.AutoCopyToClipboard);
        OptimiseVideoClipboard = SettingsHost.Get(SettingsRegistry.OptimiseVideoClipboard);
        OptimisePdfClipboard = SettingsHost.Get(SettingsRegistry.OptimisePdfClipboard);
        PreserveDates = SettingsHost.Get(SettingsRegistry.PreserveDates);
        StripMetadata = SettingsHost.Get(SettingsRegistry.StripMetadata);
        EnableAutomaticImageOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticImageOptimisations);
        EnableAutomaticVideoOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticVideoOptimisations);
        EnableAutomaticPdfOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticPdfOptimisations);
        var themeMode = SettingsHost.Get(SettingsRegistry.AppThemeMode);
        SelectedThemeOption = ThemeOptions.FirstOrDefault(option => option.Mode == themeMode) ?? ThemeOptions.FirstOrDefault();

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
            case var name when name == SettingsRegistry.FloatingHudPinned.Name:
                FloatingHudPinned = SettingsHost.Get(SettingsRegistry.FloatingHudPinned);
                break;
            case var name when name == SettingsRegistry.EnableClipboardOptimiser.Name:
                EnableClipboardOptimiser = SettingsHost.Get(SettingsRegistry.EnableClipboardOptimiser);
                break;
            case var name when name == SettingsRegistry.AutoCopyToClipboard.Name:
                AutoCopyToClipboard = SettingsHost.Get(SettingsRegistry.AutoCopyToClipboard);
                break;
            case var name when name == SettingsRegistry.OptimiseVideoClipboard.Name:
                OptimiseVideoClipboard = SettingsHost.Get(SettingsRegistry.OptimiseVideoClipboard);
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
            case var name when name == SettingsRegistry.EnableAutomaticImageOptimisations.Name:
                EnableAutomaticImageOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticImageOptimisations);
                break;
            case var name when name == SettingsRegistry.EnableAutomaticVideoOptimisations.Name:
                EnableAutomaticVideoOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticVideoOptimisations);
                break;
            case var name when name == SettingsRegistry.EnableAutomaticPdfOptimisations.Name:
                EnableAutomaticPdfOptimisations = SettingsHost.Get(SettingsRegistry.EnableAutomaticPdfOptimisations);
                break;
            case var name when name == SettingsRegistry.AppThemeMode.Name:
                var themeMode = SettingsHost.Get(SettingsRegistry.AppThemeMode);
                SelectedThemeOption = ThemeOptions.FirstOrDefault(option => option.Mode == themeMode) ?? SelectedThemeOption;
                break;
        }
        _suppressStoreUpdates = false;
    }

    public void Dispose()
    {
        SettingsHost.SettingChanged -= OnSettingChanged;
        ShortcutCatalog.ShortcutChanged -= OnShortcutChanged;
    }

    private void BeginHudPlacement()
    {
        _hudController.BeginPlacementMode();
    }

    private void ClearHudPlacement()
    {
        _hudController.ClearPinnedPlacement();
    }

    private void OnShortcutChanged(object? sender, ShortcutChangedEventArgs e)
    {
        if (_shortcutLookup.TryGetValue(e.Id, out var viewModel))
        {
            viewModel.Refresh();
        }
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
}
