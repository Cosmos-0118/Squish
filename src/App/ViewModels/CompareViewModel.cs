using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Squish.App.Localization;
using Squish.App.Infrastructure;
using Squish.App.Services;
using Squish.Core.Optimizers;
using Squish.Core.Settings;
using Squish.Core.Shared;
using Microsoft.Extensions.Logging;

namespace Squish.App.ViewModels;

public sealed class CompareViewModel : ObservableObject, IDisposable
{
    private const int MaxRecentItems = 6;

    private readonly OptimisationCoordinator _coordinator;
    private readonly FloatingHudController _hudController;
    private readonly ILogger<CompareViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, TrackedRequest> _trackedRequests = new(StringComparer.Ordinal);
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
        OpenRecentItemFolderCommand = new RelayCommand(parameter => OpenRecentItemFolder(parameter as string));

        _coordinator.RequestCompleted += OnRequestCompleted;
        _coordinator.RequestFailed += OnRequestFailed;
    }

    public RelayCommand BrowseForFilesCommand { get; }

    public RelayCommand OpenRecentItemFolderCommand { get; }

    public RelayCommand CancelActiveRequestsCommand => _cancelActiveRequestsCommand;

    public ReadOnlyObservableCollection<RecentOptimisationViewModel> RecentItems => _readonlyRecentItems;

    public bool HasActiveRequests => !_trackedRequests.IsEmpty;

    public void TriggerBrowseDialog()
    {
        ShowBrowseDialog();
    }

    public void EnqueueDroppedPaths(IEnumerable<string> paths)
    {
        EnqueuePaths(paths);
    }

    private void ShowBrowseDialog()
    {
        try
        {
            var supportedLabel = SquishStringCatalog.Get("compare.dialog.filterSupported");
            var allFilesLabel = SquishStringCatalog.Get("compare.dialog.filterAll");
            var supportedPattern = BuildSupportedFilterPattern();
            var filter = string.Format(CultureInfo.InvariantCulture, "{0}|{2}|{1}|*.*", supportedLabel, allFilesLabel, supportedPattern);

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = SquishStringCatalog.Get("compare.dialog.title"),
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
                // Optimisation rewrites the file in place, so the "before" size has to be captured now.
                _trackedRequests[request.RequestId] = new TrackedRequest(request, TryGetLength(filePath));
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

        return null;
    }

    private void OnRequestCompleted(object? sender, OptimisationCompletedEventArgs e)
    {
        if (!_trackedRequests.TryRemove(e.Result.RequestId, out var tracked))
        {
            return;
        }

        var request = tracked.Request;
        var outputPath = e.Result.OutputPath ?? request.SourcePath;
        var summary = BuildSummary(request, tracked.OriginalBytes, outputPath, e.Result);
        if (string.IsNullOrWhiteSpace(summary.SizeSummary))
        {
            // "No size change" is only true when we know the original size and it matches; otherwise stay neutral.
            var fallback = e.Result.Status == OptimisationStatus.Succeeded && tracked.OriginalBytes is not null
                ? SquishStringCatalog.Get("compare.recent.noChange")
                : SquishStringCatalog.Get("compare.recent.completed");
            summary = summary with { SizeSummary = fallback };
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

    private static string BuildSupportedFilterPattern()
    {
        static IEnumerable<string> ToPatterns(IEnumerable<string> extensions) => extensions.Select(ext => $"*.{ext}");

        var patterns = ToPatterns(MediaFormats.ImageExtensionNames)
            .Concat(ToPatterns(MediaFormats.VideoExtensionNames))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(';', patterns);
    }

    private static RecentOptimisationViewModel BuildSummary(OptimisationRequest request, long? originalBytes, FilePath outputPath, OptimisationResult result)
    {
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
        // Signed change in file size: negative when the file got smaller.
        var percentage = originalBytes == 0 ? 0 : (double)(optimisedBytes - originalBytes) / originalBytes * 100d;
        var direction = delta >= 0 ? "Saved" : "Added";
        return string.Format(CultureInfo.CurrentCulture, "{0} {1} ({2} → {3}, {4:+0.#;-0.#;0}%)", direction, changeAmount, original, final, percentage);
    }

    private readonly record struct TrackedRequest(OptimisationRequest Request, long? OriginalBytes);

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

    private static void OpenRecentItemFolder(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        try
        {
            var escaped = outputPath.Replace("\"", "\"\"");
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = $"/select,\"{escaped}\"",
                UseShellExecute = true
            };

            Process.Start(startInfo);
        }
        catch
        {
            // Ignore shell launch failures to keep the UX non-blocking.
        }
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
