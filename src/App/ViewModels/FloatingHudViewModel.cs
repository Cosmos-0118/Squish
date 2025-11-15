using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace ClopWindows.App.ViewModels;

public sealed class FloatingHudViewModel : ObservableObject
{
    private readonly ObservableCollection<FloatingResultViewModel> _results;

    public FloatingHudViewModel()
    {
        _results = new ObservableCollection<FloatingResultViewModel>();
        _results.CollectionChanged += OnResultsChanged;
    }

    public ObservableCollection<FloatingResultViewModel> Results => _results;

    public bool HasResults => _results.Count > 0;

    public double PreferredWidth => _results.Count switch
    {
        0 => 320,
        1 => 340,
        2 => 360,
        3 => 380,
        _ => 420
    };

    public double MaxResultsHeight => _results.Count switch
    {
        0 => 180,
        1 => 220,
        2 => 280,
        3 => 340,
        4 => 380,
        _ => 420
    };

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

    private void RaiseLayoutProperties()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(PreferredWidth));
        OnPropertyChanged(nameof(MaxResultsHeight));
    }
}
