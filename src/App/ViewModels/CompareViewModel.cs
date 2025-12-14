using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using ClopWindows.App.Localization;
using ClopWindows.App.Infrastructure;
using ClopWindows.App.Services;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;
using Microsoft.Extensions.Logging;

namespace ClopWindows.App.ViewModels;

public sealed class CompareViewModel : ObservableObject, IDisposable
{
    private const int MaxRecentItems = 6;

    private readonly OptimisationCoordinator _coordinator;
    private readonly FloatingHudController _hudController;
    private readonly ILogger<CompareViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, OptimisationRequest> _trackedRequests = new(StringComparer.Ordinal);
    private readonly ObservableCollection<RecentOptimisationViewModel> _recentItems = new();
    private readonly ReadOnlyObservableCollection<RecentOptimisationViewModel> _readonlyRecentItems;
    private readonly RelayCommand _cancelActiveRequestsCommand;

    public CompareViewModel(
        OptimisationCoordinator coordinator,
        FloatingHudController hudController,
        ILogger<CompareViewModel> logger)
    {
        _coordinator = coordinator;
        _hudController = hudController;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _readonlyRecentItems = new ReadOnlyObservableCollection<RecentOptimisationViewModel>(_recentItems);

        _cancelActiveRequestsCommand = new RelayCommand(_ => CancelActiveRequests(), _ => HasActiveRequests);

        BrowseForFilesCommand = new RelayCommand(_ => ShowBrowseDialog());

        _coordinator.RequestCompleted += OnRequestCompleted;
        _coordinator.RequestFailed += OnRequestFailed;
    }

    public RelayCommand BrowseForFilesCommand { get; }

    public RelayCommand CancelActiveRequestsCommand => _cancelActiveRequestsCommand;

    public ReadOnlyObservableCollection<RecentOptimisationViewModel> RecentItems => _readonlyRecentItems;

    public bool HasActiveRequests => !_trackedRequests.IsEmpty;

    public void TriggerBrowseDialog()
    {
        ShowBrowseDialog();
    }

    private void ShowBrowseDialog()
    {
        try
        {
            var supportedLabel = ClopStringCatalog.Get("compare.dialog.filterSupported");
            var allFilesLabel = ClopStringCatalog.Get("compare.dialog.filterAll");
            var supportedPattern = BuildSupportedFilterPattern();
            var filter = string.Format(CultureInfo.InvariantCulture, "{0}|{2}|{1}|*.*", supportedLabel, allFilesLabel, supportedPattern);

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = ClopStringCatalog.Get("compare.dialog.title"),
                Multiselect = true,
                Filter = filter
            };

            var result = dialog.ShowDialog();
            if (result != true)
            {
                _logger.LogInformation("Browse dialog cancelled by user.");
                return;
            }

            _logger.LogInformation("Browse dialog returned {FileCount} files", dialog.FileNames.Length);
            EnqueuePaths(dialog.FileNames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Browse dialog failed");
            throw;
        }
    }

    private void EnqueuePaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var filePath = FilePath.From(path);
                var itemType = ResolveItemType(filePath);
                if (itemType is null)
                {
                    _logger.LogInformation("Skipping unsupported file type {Path}", path);
                    continue;
                }

                var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source"] = "manual"
                };

                OutputBehaviourSettings.ApplyTo(metadata);
                VideoEncoderPresetSettings.ApplyTo(metadata);
                AggressiveOptimisationHelper.Apply(itemType.Value, filePath, metadata);

                var request = new OptimisationRequest(itemType.Value, filePath, metadata: metadata);
                _trackedRequests[request.RequestId] = request;
                NotifyActiveRequestsChanged();

                _hudController.TrackRequest(request);
                _ = _coordinator.Enqueue(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue optimisation for {Path}", path);
            }
        }
    }

    private static ItemType? ResolveItemType(FilePath path)
    {
        if (MediaFormats.IsImage(path))
        {
            return ItemType.Image;
        }

        if (MediaFormats.IsVideo(path))
        {
            return ItemType.Video;
        }

        if (MediaFormats.IsPdf(path))
        {
            return ItemType.Pdf;
        }

        if (ShouldConvertDocuments() && MediaFormats.IsDocument(path))
        {
            return ItemType.Document;
        }

        return null;
    }

    private void OnRequestCompleted(object? sender, OptimisationCompletedEventArgs e)
    {
        if (!_trackedRequests.TryRemove(e.Result.RequestId, out var request))
        {
            return;
        }

        var outputPath = e.Result.OutputPath ?? request.SourcePath;
        var summary = BuildSummary(request, outputPath, e.Result);

        if (string.IsNullOrWhiteSpace(summary.SizeSummary))
        {
            return;
        }

        _dispatcher.Invoke(() =>
        {
            NotifyActiveRequestsChanged();

            if (e.Result.Status != OptimisationStatus.Succeeded)
            {
                return;
            }

            _recentItems.Insert(0, summary);
            while (_recentItems.Count > MaxRecentItems)
            {
                _recentItems.RemoveAt(_recentItems.Count - 1);
            }
            OnPropertyChanged(nameof(RecentItems));
        });
    }

    private void OnRequestFailed(object? sender, OptimisationCompletedEventArgs e)
    {
        if (!_trackedRequests.TryRemove(e.Result.RequestId, out _))
        {
            return;
        }

        _dispatcher.Invoke(NotifyActiveRequestsChanged);
    }

    private static bool ShouldConvertDocuments() => SettingsHost.Get(SettingsRegistry.AutoConvertDocumentsToPdf);

    private static string BuildSupportedFilterPattern()
    {
        static IEnumerable<string> ToPatterns(IEnumerable<string> extensions) => extensions.Select(ext => $"*.{ext}");

        var patterns = ToPatterns(MediaFormats.ImageExtensionNames)
            .Concat(ToPatterns(MediaFormats.VideoExtensionNames))
            .Concat(ToPatterns(MediaFormats.PdfExtensionNames))
            .Concat(ToPatterns(MediaFormats.DocumentExtensionNames))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(';', patterns);
    }

    private static RecentOptimisationViewModel BuildSummary(OptimisationRequest request, FilePath outputPath, OptimisationResult result)
    {
        var originalBytes = TryGetLength(request.SourcePath);
        var optimisedBytes = TryGetLength(outputPath);
        var sizeSummary = originalBytes is not null && optimisedBytes is not null
            ? BuildSizeSummary(originalBytes.Value, optimisedBytes.Value)
            : string.Empty;

        var durationText = result.Duration.HasValue
            ? result.Duration.Value.ToString(result.Duration.Value.TotalMinutes >= 1 ? "m\\:ss" : "s\\:ff", CultureInfo.CurrentCulture)
            : null;

        return new RecentOptimisationViewModel(
            request.SourcePath.Name,
            sizeSummary,
            result.Duration,
            outputPath.Value,
            durationText);
    }

    private static string BuildSizeSummary(long originalBytes, long optimisedBytes)
    {
        var delta = originalBytes - optimisedBytes;
        if (delta == 0)
        {
            return string.Empty;
        }

        var magnitude = Math.Abs(delta);
        var original = originalBytes.HumanSize();
        var final = optimisedBytes.HumanSize();
        var changeAmount = magnitude.HumanSize();
        var percentage = originalBytes == 0 ? 0 : (double)delta / originalBytes * 100d;
        var direction = delta >= 0 ? "Saved" : "Added";
        return string.Format(CultureInfo.CurrentCulture, "{0} {1} ({2} → {3}, {4:+0.##;-0.##;0}%)", direction, changeAmount, original, final, percentage);
    }

    private static long? TryGetLength(FilePath path)
    {
        try
        {
            var info = path.ToFileInfo();
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    private void CancelActiveRequests()
    {
        foreach (var requestId in _trackedRequests.Keys.ToArray())
        {
            _coordinator.Cancel(requestId, "Cancelled by user");
        }

        NotifyActiveRequestsChanged();
    }

    private void NotifyActiveRequestsChanged()
    {
        OnPropertyChanged(nameof(HasActiveRequests));
        _cancelActiveRequestsCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _coordinator.RequestCompleted -= OnRequestCompleted;
        _coordinator.RequestFailed -= OnRequestFailed;
    }
}

public sealed record RecentOptimisationViewModel(string FileName, string SizeSummary, TimeSpan? Duration, string OutputPath, string? DurationText);
