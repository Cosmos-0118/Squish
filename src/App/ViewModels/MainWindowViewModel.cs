using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Squish.App.Infrastructure;
using Squish.App.Localization;
using Squish.Core.Settings;

namespace Squish.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const double CompactLayoutBreakpoint = 860;

    private readonly OnboardingViewModel _onboardingViewModel;
    private readonly CompareViewModel _compareViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly NavigationItemViewModel _onboardingItem;
    private readonly NavigationItemViewModel _compareItem;
    private readonly NavigationItemViewModel _settingsItem;
    private NavigationItemViewModel? _selectedItem;
    private ObservableObject? _currentView;
    private bool _isSidebarCollapsed;
    private bool _isCompactLayout;

    public MainWindowViewModel(
        OnboardingViewModel onboardingViewModel,
        CompareViewModel compareViewModel,
        SettingsViewModel settingsViewModel)
    {
        _onboardingViewModel = onboardingViewModel;
        _compareViewModel = compareViewModel;
        _settingsViewModel = settingsViewModel;

        NavigationItems = new ObservableCollection<NavigationItemViewModel>
        {
            new NavigationItemViewModel(
                MainSection.Onboarding,
                SquishStringCatalog.Get("navigation.onboarding.title"),
                SquishStringCatalog.Get("navigation.onboarding.subtitle")),
            new NavigationItemViewModel(
                MainSection.Compare,
                SquishStringCatalog.Get("navigation.compare.title"),
                SquishStringCatalog.Get("navigation.compare.subtitle")),
            new NavigationItemViewModel(
                MainSection.Settings,
                SquishStringCatalog.Get("navigation.settings.title"),
                SquishStringCatalog.Get("navigation.settings.subtitle"))
        };

        _onboardingItem = NavigationItems.First(item => item.Section == MainSection.Onboarding);
        _compareItem = NavigationItems.First(item => item.Section == MainSection.Compare);
        _settingsItem = NavigationItems.First(item => item.Section == MainSection.Settings);

        ShowOnboardingCommand = new RelayCommand(_ => SelectedItem = _onboardingItem);
        ShowCompareCommand = new RelayCommand(_ => SelectedItem = _compareItem);
        ShowSettingsCommand = new RelayCommand(_ => SelectedItem = _settingsItem);
        BrowseFilesCommand = new RelayCommand(_ =>
        {
            SelectedItem = _compareItem;
            _compareViewModel.TriggerBrowseDialog();
        });

        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarCollapsed = !IsSidebarCollapsed);

        _onboardingViewModel.OnboardingCompleted += HandleOnboardingCompleted;

        var defaultSection = SettingsHost.Get(SettingsRegistry.FinishedOnboarding)
            ? MainSection.Compare
            : MainSection.Onboarding;

        SelectedItem = NavigationItems.First(item => item.Section == defaultSection);
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public CompareViewModel Compare => _compareViewModel;

    public ICommand ShowOnboardingCommand { get; }

    public ICommand ShowCompareCommand { get; }

    public ICommand ShowSettingsCommand { get; }

    public ICommand BrowseFilesCommand { get; }

    public ICommand ToggleSidebarCommand { get; }

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (SetProperty(ref _isSidebarCollapsed, value))
            {
                OnPropertyChanged(nameof(IsSidebarRail));
            }
        }
    }

    /// <summary>True when the window is too narrow for the full sidebar and page padding.</summary>
    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set
        {
            if (SetProperty(ref _isCompactLayout, value))
            {
                OnPropertyChanged(nameof(IsSidebarRail));
            }
        }
    }

    /// <summary>True when the sidebar shows icons only, either by choice or because the window is narrow.</summary>
    public bool IsSidebarRail => IsSidebarCollapsed || IsCompactLayout;

    public void UpdateLayoutWidth(double windowWidth)
    {
        IsCompactLayout = windowWidth < CompactLayoutBreakpoint;
    }

    public NavigationItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
            {
                return;
            }

            if (value is null)
            {
                CurrentView = null;
                return;
            }

            CurrentView = ResolveView(value.Section);
        }
    }

    public ObservableObject? CurrentView
    {
        get => _currentView;
        private set => SetProperty(ref _currentView, value);
    }

    private ObservableObject ResolveView(MainSection section) => section switch
    {
        MainSection.Onboarding => _onboardingViewModel,
        MainSection.Compare => _compareViewModel,
        MainSection.Settings => _settingsViewModel,
        _ => _onboardingViewModel
    };

    private void HandleOnboardingCompleted(object? sender, EventArgs e)
    {
        SelectedItem = _compareItem;
    }

    public void Dispose()
    {
        _onboardingViewModel.OnboardingCompleted -= HandleOnboardingCompleted;
    }
}

public enum MainSection
{
    Onboarding,
    Compare,
    Settings
}

public sealed class NavigationItemViewModel
{
    public NavigationItemViewModel(MainSection section, string title, string subtitle)
    {
        Section = section;
        Title = title;
        Subtitle = subtitle;
    }

    public MainSection Section { get; }

    public string Title { get; }

    public string Subtitle { get; }
}
