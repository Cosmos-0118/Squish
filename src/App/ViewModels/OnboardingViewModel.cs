using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using Squish.App.Infrastructure;
using Squish.App.Localization;
using Squish.Core.Settings;

namespace Squish.App.ViewModels;

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
                SquishStringCatalog.Get("onboarding.step.1.title"),
                SquishStringCatalog.Get("onboarding.step.1.description"),
                new[]
                {
                    "Use Browse files or drag and drop files directly into the window.",
                    "You can queue multiple images and videos in one action.",
                    "Squish starts processing immediately and tracks each item live."
                }),
            new(
                SquishStringCatalog.Get("onboarding.step.2.title"),
                SquishStringCatalog.Get("onboarding.step.2.description"),
                new[]
                {
                    "Watch each result in Recent Results after optimization completes.",
                    "Check saved size and final output summary before sharing.",
                    "Hover any entry and open it directly in its folder."
                }),
            new(
                SquishStringCatalog.Get("onboarding.step.3.title"),
                SquishStringCatalog.Get("onboarding.step.3.description"),
                new[]
                {
                    "Open output files from recent results for quick validation.",
                    "Send smaller files faster through chat, email, or docs.",
                    "Keep quality while reducing upload and sync time."
                }),
            new(
                SquishStringCatalog.Get("onboarding.step.4.title"),
                SquishStringCatalog.Get("onboarding.step.4.description"),
                new[]
                {
                    "Enable Clipboard and watched folders in Settings.",
                    "Pick image and video folders for automatic optimization.",
                    "Tune output and preservation options for your workflow."
                })
        };

        _readonlySteps = new ReadOnlyObservableCollection<OnboardingStepViewModel>(_steps);
        _selectedStep = _steps.FirstOrDefault();

        GetStartedCommand = new RelayCommand(_ => CompleteOnboarding());
        NextStepCommand = new RelayCommand(_ => MoveStep(1), _ => !IsLastStep);
        PreviousStepCommand = new RelayCommand(_ => MoveStep(-1), _ => SelectedStepIndex > 0);
        _hasCompleted = SettingsHost.Get(SettingsRegistry.FinishedOnboarding);
    }

    public event EventHandler? OnboardingCompleted;

    public string Title => SquishStringCatalog.Get("onboarding.title");

    public string Subtitle => SquishStringCatalog.Get("onboarding.subtitle");

    public ReadOnlyObservableCollection<OnboardingStepViewModel> Steps => _readonlySteps;

    public OnboardingStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (!SetProperty(ref _selectedStep, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StepPosition));
            OnPropertyChanged(nameof(IsLastStep));
            NextStepCommand.RaiseCanExecuteChanged();
            PreviousStepCommand.RaiseCanExecuteChanged();
        }
    }

    public string StepPosition => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        SquishStringCatalog.Get("onboarding.stepPosition"),
        SelectedStepIndex + 1,
        _steps.Count);

    public bool IsLastStep => SelectedStepIndex >= _steps.Count - 1;

    private int SelectedStepIndex => _selectedStep is null ? 0 : _steps.IndexOf(_selectedStep);

    public bool HasCompleted
    {
        get => _hasCompleted;
        private set => SetProperty(ref _hasCompleted, value);
    }

    public RelayCommand GetStartedCommand { get; }

    public RelayCommand NextStepCommand { get; }

    public RelayCommand PreviousStepCommand { get; }

    private void MoveStep(int delta)
    {
        var index = Math.Clamp(SelectedStepIndex + delta, 0, _steps.Count - 1);
        SelectedStep = _steps[index];
    }

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
