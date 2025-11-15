using System;
using System.Collections.ObjectModel;
using ClopWindows.App.Infrastructure;
using ClopWindows.App.Localization;
using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class OnboardingViewModel : ObservableObject
{
    private readonly ObservableCollection<OnboardingStepViewModel> _steps;
    private readonly ReadOnlyObservableCollection<OnboardingStepViewModel> _readonlySteps;
    private bool _hasCompleted;

    public OnboardingViewModel()
    {
        _steps = new ObservableCollection<OnboardingStepViewModel>
        {
            new(
                ClopStringCatalog.Get("onboarding.step.1.title"),
                ClopStringCatalog.Get("onboarding.step.1.description")),
            new(
                ClopStringCatalog.Get("onboarding.step.2.title"),
                ClopStringCatalog.Get("onboarding.step.2.description")),
            new(
                ClopStringCatalog.Get("onboarding.step.3.title"),
                ClopStringCatalog.Get("onboarding.step.3.description")),
            new(
                ClopStringCatalog.Get("onboarding.step.4.title"),
                ClopStringCatalog.Get("onboarding.step.4.description"))
        };

        _readonlySteps = new ReadOnlyObservableCollection<OnboardingStepViewModel>(_steps);

        GetStartedCommand = new RelayCommand(_ => CompleteOnboarding());
        _hasCompleted = SettingsHost.Get(SettingsRegistry.FinishedOnboarding);
    }

    public event EventHandler? OnboardingCompleted;

    public string Title => ClopStringCatalog.Get("onboarding.title");

    public string Subtitle => ClopStringCatalog.Get("onboarding.subtitle");

    public ReadOnlyObservableCollection<OnboardingStepViewModel> Steps => _readonlySteps;

    public bool HasCompleted
    {
        get => _hasCompleted;
        private set => SetProperty(ref _hasCompleted, value);
    }

    public RelayCommand GetStartedCommand { get; }

    private void CompleteOnboarding()
    {
        SettingsHost.Set(SettingsRegistry.FinishedOnboarding, true);
        HasCompleted = true;
        OnboardingCompleted?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class OnboardingStepViewModel
{
    public OnboardingStepViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }

    public string Description { get; }
}
