using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using ClopWindows.App.Infrastructure;
using ClopWindows.App.Localization;
using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class OnboardingViewModel : ObservableObject
{
    private readonly ObservableCollection<OnboardingStepViewModel> _steps;
    private readonly ReadOnlyObservableCollection<OnboardingStepViewModel> _readonlySteps;
    private bool _hasCompleted;
    private OnboardingStepViewModel? _selectedStep;

    public OnboardingViewModel()
    {
        _steps = new ObservableCollection<OnboardingStepViewModel>
        {
            new(
                ClopStringCatalog.Get("onboarding.step.1.title"),
                ClopStringCatalog.Get("onboarding.step.1.description"),
                new[]
                {
                    "Use Browse files or drag and drop files directly into the window.",
                    "You can queue multiple images, videos, and PDFs in one action.",
                    "Clop starts processing immediately and tracks each item live."
                }),
            new(
                ClopStringCatalog.Get("onboarding.step.2.title"),
                ClopStringCatalog.Get("onboarding.step.2.description"),
                new[]
                {
                    "Watch each result in Recent Results after optimization completes.",
                    "Check saved size and final output summary before sharing.",
                    "Hover any entry and open it directly in its folder."
                }),
            new(
                ClopStringCatalog.Get("onboarding.step.3.title"),
                ClopStringCatalog.Get("onboarding.step.3.description"),
                new[]
                {
                    "Open output files from recent results for quick validation.",
                    "Send smaller files faster through chat, email, or docs.",
                    "Keep quality while reducing upload and sync time."
                }),
            new(
                ClopStringCatalog.Get("onboarding.step.4.title"),
                ClopStringCatalog.Get("onboarding.step.4.description"),
                new[]
                {
                    "Enable Clipboard and watched folders in Settings.",
                    "Pick image, video, and PDF folders for automatic optimization.",
                    "Tune output and preservation options for your workflow."
                })
        };

        _readonlySteps = new ReadOnlyObservableCollection<OnboardingStepViewModel>(_steps);
        _selectedStep = _steps.FirstOrDefault();

        GetStartedCommand = new RelayCommand(_ => CompleteOnboarding());
        _hasCompleted = SettingsHost.Get(SettingsRegistry.FinishedOnboarding);
    }

    public event EventHandler? OnboardingCompleted;

    public string Title => ClopStringCatalog.Get("onboarding.title");

    public string Subtitle => ClopStringCatalog.Get("onboarding.subtitle");

    public ReadOnlyObservableCollection<OnboardingStepViewModel> Steps => _readonlySteps;

    public OnboardingStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set => SetProperty(ref _selectedStep, value);
    }

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
    public OnboardingStepViewModel(string title, string description, IEnumerable<string> details)
    {
        Title = title;
        Description = description;
        Details = details?.ToArray() ?? Array.Empty<string>();
    }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<string> Details { get; }
}
