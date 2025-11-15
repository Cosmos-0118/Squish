using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class FloatingHudViewModel : ObservableObject
{
    public const double MinScaleValue = 0.4;
    public const double MaxScaleValue = 2.0;

    private readonly ObservableCollection<FloatingResultViewModel> _results;
    private double _widthScale;
    private double _heightScale;
    private double _persistedWidthScale;
    private double _persistedHeightScale;
    private double _preferredWidth;
    private double _maxResultsHeight;

    public FloatingHudViewModel()
    {
        SettingsHost.EnsureInitialized();
        _results = new ObservableCollection<FloatingResultViewModel>();
        _results.CollectionChanged += OnResultsChanged;
        var storedWidthScale = SettingsHost.Get(SettingsRegistry.FloatingHudWidthScale);
        var storedHeightScale = SettingsHost.Get(SettingsRegistry.FloatingHudHeightScale);
        var legacyScale = SettingsHost.Get(SettingsRegistry.FloatingHudScale);

        if (Math.Abs(storedWidthScale - 1f) < 0.001 && Math.Abs(storedHeightScale - 1f) < 0.001 && Math.Abs(legacyScale - 1f) > 0.001)
        {
            storedWidthScale = legacyScale;
            storedHeightScale = legacyScale;
        }

        _persistedWidthScale = ClampScale(storedWidthScale);
        _persistedHeightScale = ClampScale(storedHeightScale);
        _widthScale = _persistedWidthScale;
        _heightScale = _persistedHeightScale;
        UpdateLayoutMetrics();
    }

    public ObservableCollection<FloatingResultViewModel> Results => _results;

    public bool HasResults => _results.Count > 0;

    public double WidthScale => _widthScale;

    public double HeightScale => _heightScale;

    public double MinScale => MinScaleValue;

    public double MaxScale => MaxScaleValue;

    public double PreferredWidth
    {
        get => _preferredWidth;
        private set
        {
            if (SetProperty(ref _preferredWidth, value))
            {
                OnPropertyChanged(nameof(DisplayWidth));
            }
        }
    }

    public double MaxResultsHeight
    {
        get => _maxResultsHeight;
        private set
        {
            if (SetProperty(ref _maxResultsHeight, value))
            {
                OnPropertyChanged(nameof(DisplayMaxResultsHeight));
            }
        }
    }

    public double DisplayWidth => _preferredWidth * _widthScale;

    public double DisplayMaxResultsHeight => _maxResultsHeight * _heightScale;

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RaiseLayoutProperties();
    }

    internal void InsertResult(FloatingResultViewModel viewModel)
    {
        _results.Insert(0, viewModel);
        RaiseLayoutProperties();
    }

    internal void RemoveResult(FloatingResultViewModel viewModel)
    {
        _results.Remove(viewModel);
        RaiseLayoutProperties();
    }

    internal void ClearResults()
    {
        _results.Clear();
        RaiseLayoutProperties();
    }

    internal void PreviewWidthScale(double scale)
    {
        var clamped = ClampScale(scale);
        if (Math.Abs(clamped - _widthScale) < 0.001)
        {
            return;
        }

        _widthScale = clamped;
        OnPropertyChanged(nameof(WidthScale));
        OnPropertyChanged(nameof(DisplayWidth));
    }

    internal void PreviewHeightScale(double scale)
    {
        var clamped = ClampScale(scale);
        if (Math.Abs(clamped - _heightScale) < 0.001)
        {
            return;
        }

        _heightScale = clamped;
        OnPropertyChanged(nameof(HeightScale));
        OnPropertyChanged(nameof(DisplayMaxResultsHeight));
    }

    internal void CommitScales()
    {
        _persistedWidthScale = _widthScale;
        _persistedHeightScale = _heightScale;
        SettingsHost.Set(SettingsRegistry.FloatingHudWidthScale, (float)_persistedWidthScale);
        SettingsHost.Set(SettingsRegistry.FloatingHudHeightScale, (float)_persistedHeightScale);
        SettingsHost.Set(SettingsRegistry.FloatingHudScale, (float)(_widthScale + _heightScale) / 2f);
    }

    internal void RevertScales()
    {
        var widthChanged = Math.Abs(_widthScale - _persistedWidthScale) >= 0.001;
        var heightChanged = Math.Abs(_heightScale - _persistedHeightScale) >= 0.001;

        if (!widthChanged && !heightChanged)
        {
            return;
        }

        _widthScale = _persistedWidthScale;
        _heightScale = _persistedHeightScale;
        OnPropertyChanged(nameof(WidthScale));
        OnPropertyChanged(nameof(HeightScale));
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayMaxResultsHeight));
    }

    private void RaiseLayoutProperties()
    {
        OnPropertyChanged(nameof(HasResults));
        UpdateLayoutMetrics();
    }

    private void UpdateLayoutMetrics()
    {
        PreferredWidth = BaseWidthForCount(_results.Count);
        MaxResultsHeight = BaseHeightForCount(_results.Count);
    }

    private static double ClampScale(double scale) => Math.Clamp(scale <= 0 ? 1 : scale, MinScaleValue, MaxScaleValue);

    private static double BaseWidthForCount(int count) => count switch
    {
        <= 0 => 280,
        1 => 300,
        2 => 330,
        3 => 360,
        _ => 380
    };

    private static double BaseHeightForCount(int count) => count switch
    {
        0 => 120,
        1 => 160,
        2 => 210,
        3 => 260,
        4 => 300,
        _ => 320
    };
}
