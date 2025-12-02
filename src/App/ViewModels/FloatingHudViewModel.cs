using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class FloatingHudViewModel : ObservableObject
{
    public const double MinWindowWidth = 280d;
    public const double MaxWindowWidth = 640d;
    public const double MinWindowHeight = 180d;
    public const double MaxWindowHeight = 520d;

    private readonly ObservableCollection<FloatingResultViewModel> _results;
    private FloatingResultViewModel? _currentResult;
    private double _windowWidth;
    private double _windowHeight;
    private bool _suppressSettingsSync;

    public FloatingHudViewModel()
    {
        SettingsHost.EnsureInitialized();
        _results = new ObservableCollection<FloatingResultViewModel>();
        _results.CollectionChanged += OnResultsChanged;

        _windowWidth = LoadDimension(
            SettingsRegistry.FloatingHudWidth,
            SettingsRegistry.DefaultFloatingHudWidth,
            MinWindowWidth,
            MaxWindowWidth,
            SettingsRegistry.FloatingHudWidthScale);

        _windowHeight = LoadDimension(
            SettingsRegistry.FloatingHudHeight,
            SettingsRegistry.DefaultFloatingHudHeight,
            MinWindowHeight,
            MaxWindowHeight,
            SettingsRegistry.FloatingHudHeightScale);

        SettingsHost.SettingChanged += OnSettingChanged;
    }

    public ObservableCollection<FloatingResultViewModel> Results => _results;

    public bool HasResults => _results.Count > 0;

    public FloatingResultViewModel? CurrentResult
    {
        get => _currentResult;
        private set
        {
            if (SetProperty(ref _currentResult, value))
            {
                OnPropertyChanged(nameof(HasCurrentResult));
            }
        }
    }

    public bool HasCurrentResult => CurrentResult is not null;

    public double WindowWidth
    {
        get => _windowWidth;
        set => UpdateDimension(ref _windowWidth, value, MinWindowWidth, MaxWindowWidth, SettingsRegistry.FloatingHudWidth);
    }

    public double WindowHeight
    {
        get => _windowHeight;
        set => UpdateDimension(ref _windowHeight, value, MinWindowHeight, MaxWindowHeight, SettingsRegistry.FloatingHudHeight);
    }

    private void UpdateDimension(ref double field, double rawValue, double min, double max, SettingKey<double> key)
    {
        var normalized = double.IsNaN(rawValue) ? min : rawValue;
        var clamped = Math.Clamp(normalized, min, max);

        if (SetProperty(ref field, clamped) && !_suppressSettingsSync)
        {
            SettingsHost.Set(key, clamped);
        }
    }

    private static double LoadDimension(SettingKey<double> key, double fallback, double min, double max, SettingKey<float> legacyScale)
    {
        var value = SettingsHost.Get(key);

        if (value <= 0)
        {
            value = fallback;
        }

        if (Math.Abs(value - fallback) < 0.01)
        {
            var scale = SettingsHost.Get(legacyScale);
            if (Math.Abs(scale - 1f) > 0.01f)
            {
                value = fallback * scale;
            }
        }

        return Math.Clamp(value, min, max);
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCurrentResult();
    }

    internal void InsertResult(FloatingResultViewModel viewModel)
    {
        _results.Insert(0, viewModel);
        UpdateCurrentResult();
    }

    internal void RemoveResult(FloatingResultViewModel viewModel)
    {
        _results.Remove(viewModel);
        UpdateCurrentResult();
    }

    internal void ClearResults()
    {
        _results.Clear();
        UpdateCurrentResult();
    }

    internal void ResetLayout()
    {
        try
        {
            _suppressSettingsSync = true;
            WindowWidth = SettingsRegistry.DefaultFloatingHudWidth;
            WindowHeight = SettingsRegistry.DefaultFloatingHudHeight;
        }
        finally
        {
            _suppressSettingsSync = false;
        }

        SettingsHost.Set(SettingsRegistry.FloatingHudWidth, WindowWidth);
        SettingsHost.Set(SettingsRegistry.FloatingHudHeight, WindowHeight);
        SettingsHost.Set(SettingsRegistry.FloatingHudPlacement, FloatingHudPlacement.TopRight);
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        _suppressSettingsSync = true;

        try
        {
            if (string.Equals(e.Name, SettingsRegistry.FloatingHudWidth.Name, StringComparison.Ordinal))
            {
                WindowWidth = SettingsHost.Get(SettingsRegistry.FloatingHudWidth);
            }
            else if (string.Equals(e.Name, SettingsRegistry.FloatingHudHeight.Name, StringComparison.Ordinal))
            {
                WindowHeight = SettingsHost.Get(SettingsRegistry.FloatingHudHeight);
            }
        }
        finally
        {
            _suppressSettingsSync = false;
        }
    }

    private void UpdateCurrentResult()
    {
        CurrentResult = _results.Count > 0 ? _results[0] : null;
        OnPropertyChanged(nameof(HasResults));
    }
}
