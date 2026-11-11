using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using Squish.App.Infrastructure;
using Squish.App.Localization;
using Squish.App.Services;
using Squish.Core.Shared;
using Squish.Core.Stats;

namespace Squish.App.ViewModels;

/// <summary>Everything the Summary page shows: headline numbers, a per-type breakdown and a daily activity chart.</summary>
public sealed class SummaryViewModel : ObservableObject, IDisposable
{

    private readonly OptimisationStatsRecorder _recorder;
    private readonly Dispatcher _dispatcher;
    private StatsRange _range = StatsRange.Last30Days;
    private bool _refreshQueued;
    private bool _hasData;
    private string _filesText = "0";
    private string _savedText = "0 B";
    private string _reductionText = "–";
    private string _timeText = "–";
    private string _originalText = "0 B";
    private string _optimisedText = "0 B";
    private string _chartPeakText = string.Empty;
    private string _chartStartLabel = string.Empty;
    private string _chartEndLabel = string.Empty;
    private double _reductionPercent;

    public SummaryViewModel(OptimisationStatsRecorder recorder)
    {
        _recorder = recorder;
        _dispatcher = Dispatcher.CurrentDispatcher;

        SelectRangeCommand = new RelayCommand(parameter =>
        {
            if (parameter is string text && Enum.TryParse<StatsRange>(text, out var range))
            {
                Range = range;
            }
        });

        ClearCommand = new RelayCommand(_ => _recorder.Clear());

        _recorder.RecordAdded += OnRecordAdded;
        Refresh();
    }

    public RelayCommand SelectRangeCommand { get; }

    public RelayCommand ClearCommand { get; }

    public ObservableCollection<KindRowViewModel> Kinds { get; } = new();

    public ObservableCollection<DayBarViewModel> Days { get; } = new();

    public StatsRange Range
    {
        get => _range;
        private set
        {
            if (SetProperty(ref _range, value))
            {
                OnPropertyChanged(nameof(IsLast7Days));
                OnPropertyChanged(nameof(IsLast30Days));
                OnPropertyChanged(nameof(IsAllTime));
                Refresh();
            }
        }
    }

    public bool IsLast7Days => _range == StatsRange.Last7Days;

    public bool IsLast30Days => _range == StatsRange.Last30Days;

    public bool IsAllTime => _range == StatsRange.AllTime;

    public bool HasData
    {
        get => _hasData;
        private set
        {
            if (SetProperty(ref _hasData, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => !_hasData;

    public string FilesText
    {
        get => _filesText;
        private set => SetProperty(ref _filesText, value);
    }

    public string SavedText
    {
        get => _savedText;
        private set => SetProperty(ref _savedText, value);
    }

    public string ReductionText
    {
        get => _reductionText;
        private set => SetProperty(ref _reductionText, value);
    }

    /// <summary>0–100, drives the reduction meter.</summary>
    public double ReductionPercent
    {
        get => _reductionPercent;
        private set => SetProperty(ref _reductionPercent, value);
    }

    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    public string OriginalText
    {
        get => _originalText;
        private set => SetProperty(ref _originalText, value);
    }

    public string OptimisedText
    {
        get => _optimisedText;
        private set => SetProperty(ref _optimisedText, value);
    }

    public string ChartPeakText
    {
        get => _chartPeakText;
        private set => SetProperty(ref _chartPeakText, value);
    }

    public string ChartStartLabel
    {
        get => _chartStartLabel;
        private set => SetProperty(ref _chartStartLabel, value);
    }

    public string ChartEndLabel
    {
        get => _chartEndLabel;
        private set => SetProperty(ref _chartEndLabel, value);
    }

    private void OnRecordAdded(object? sender, EventArgs e)
    {
        // A batch of 100 files finishes in a burst; one refresh after the burst is enough.
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _ = _dispatcher.InvokeAsync(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var summary = StatsSummary.Build(_recorder.Snapshot(), _range, DateTimeOffset.Now);
        var culture = CultureInfo.CurrentCulture;

        HasData = summary.Files > 0;
        FilesText = summary.Files.ToString("N0", culture);
        SavedText = summary.SavedBytes.HumanSize();
        OriginalText = summary.OriginalBytes.HumanSize();
        OptimisedText = summary.OptimisedBytes.HumanSize();
        ReductionPercent = Math.Round(summary.SavedFraction * 100d, 1);
        ReductionText = summary.OriginalBytes > 0
            ? string.Format(culture, "{0:0.#}%", summary.SavedFraction * 100d)
            : "–";
        TimeText = summary.Files > 0 ? FormatDuration(summary.TimeSpent) : "–";

        var totalFiles = Math.Max(1, summary.Files);
        Kinds.Clear();
        foreach (var kind in summary.ByKind)
        {
            Kinds.Add(new KindRowViewModel(
                SquishStringCatalog.Get($"summary.kind.{kind.Kind.ToString().ToLowerInvariant()}"),
                GlyphFor(kind.Kind),
                kind.Files.ToString("N0", culture),
                kind.Files == 0 ? "–" : kind.SavedBytes.HumanSize(),
                100d * kind.Files / totalFiles));
        }

        var peak = summary.Days.Count == 0 ? 0 : summary.Days.Max(d => d.SavedBytes);
        Days.Clear();
        foreach (var day in summary.Days)
        {
            // Share of the tallest day (0–1); the view turns it into pixels for whatever height the chart has.
            var fraction = day.Files == 0 || peak <= 0 ? 0d : Math.Max(0.02d, (double)day.SavedBytes / peak);
            var tooltip = day.Files == 0
                ? string.Format(culture, SquishStringCatalog.Get("summary.chart.tooltipEmpty"), day.Day)
                : string.Format(culture, SquishStringCatalog.Get("summary.chart.tooltip"), day.Day, day.Files, day.SavedBytes.HumanSize());
            Days.Add(new DayBarViewModel(fraction, day.Files > 0, tooltip));
        }

        ChartPeakText = peak > 0 ? string.Format(culture, SquishStringCatalog.Get("summary.chart.peak"), peak.HumanSize()) : string.Empty;
        ChartStartLabel = summary.Days.Count > 0 ? summary.Days[0].Day.ToString("d MMM", culture) : string.Empty;
        ChartEndLabel = summary.Days.Count > 0 ? summary.Days[^1].Day.ToString("d MMM", culture) : string.Empty;
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 1)
        {
            return "<1s";
        }

        if (span.TotalMinutes < 1)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0}s", (int)span.TotalSeconds);
        }

        if (span.TotalHours < 1)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0}m {1}s", (int)span.TotalMinutes, span.Seconds);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0}h {1}m", (int)span.TotalHours, span.Minutes);
    }

    // Segoe Fluent Icons: Photo2, Video, PDF, Document.
    private static string GlyphFor(StatsKind kind) => kind switch
    {
        StatsKind.Image => "",
        StatsKind.Video => "",
        StatsKind.Pdf => "",
        _ => ""
    };

    public void Dispose()
    {
        _recorder.RecordAdded -= OnRecordAdded;
    }
}

public sealed record KindRowViewModel(string Name, string Glyph, string FilesText, string SavedText, double SharePercent);

public sealed record DayBarViewModel(double Fraction, bool HasActivity, string Tooltip);
