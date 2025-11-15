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
    private double _scale;
    private double _persistedScale;

    public FloatingHudViewModel()
    {
        SettingsHost.EnsureInitialized();
        _results = new ObservableCollection<FloatingResultViewModel>();
        _results.CollectionChanged += OnResultsChanged;
        var storedScale = SettingsHost.Get(SettingsRegistry.FloatingHudScale);
        _persistedScale = ClampScale(storedScale);
        _scale = _persistedScale;
    }

    public ObservableCollection<FloatingResultViewModel> Results => _results;

    public bool HasResults => _results.Count > 0;

    public double SizeScale => _scale;

    public double MinScale => MinScaleValue;

    public double MaxScale => MaxScaleValue;

    public double PreferredWidth => BaseWidthForCount(_results.Count) * _scale;

    public double MaxResultsHeight => BaseHeightForCount(_results.Count) * _scale;

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

    internal void PreviewScale(double scale)
    {
        var clamped = ClampScale(scale);
        if (Math.Abs(clamped - _scale) < 0.001)
        {
            return;
        }

        _scale = clamped;
        RaiseLayoutProperties();
        OnPropertyChanged(nameof(SizeScale));
    }

    internal void CommitScale()
    {
        _persistedScale = _scale;
        SettingsHost.Set(SettingsRegistry.FloatingHudScale, (float)_persistedScale);
    }

    internal void RevertScale()
    {
        if (Math.Abs(_scale - _persistedScale) < 0.001)
        {
            return;
        }

        _scale = _persistedScale;
        RaiseLayoutProperties();
        OnPropertyChanged(nameof(SizeScale));
    }

    private void RaiseLayoutProperties()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(PreferredWidth));
        OnPropertyChanged(nameof(MaxResultsHeight));
    }

    private static double ClampScale(double scale) => Math.Clamp(scale <= 0 ? 1 : scale, MinScaleValue, MaxScaleValue);

    private static double BaseWidthForCount(int count) => count switch
    {
        0 => 220,
        1 => 230,
        2 => 240,
        3 => 250,
        _ => 260
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
