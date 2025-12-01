using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ClopWindows.App.ViewModels;
using ClopWindows.App.Views.FloatingHud;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;
using Microsoft.Extensions.Logging;

namespace ClopWindows.App.Services;

public sealed class FloatingHudController : IDisposable
{
    private const int MaxVisibleResults = 6;

    private readonly FloatingHudViewModel _viewModel;
    private readonly FloatingHudWindow _window;
    private readonly OptimisationCoordinator _coordinator;
    private readonly ILogger<FloatingHudController> _logger;
    private readonly Dictionary<string, FloatingResultViewModel> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OptimisationRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _dismissDelays = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingDismissals = new(StringComparer.Ordinal);

    private bool _isInitialized;
    private bool _isDisposed;
    private bool _overlayVisible;

    public FloatingHudController(
        FloatingHudViewModel viewModel,
        FloatingHudWindow window,
        OptimisationCoordinator coordinator,
        ILogger<FloatingHudController> logger)
    {
        _viewModel = viewModel;
        _window = window;
        _coordinator = coordinator;
        _logger = logger;
    }

    public void Initialize()
    {
        ThrowIfDisposed();

        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        _window.DataContext = _viewModel;
        _window.ExternalFilesDropped += OnExternalFilesDropped;
        _window.DropOverlayVisibilityChanged += OnDropOverlayVisibilityChanged;

        _coordinator.ProgressChanged += OnProgressChanged;
        _coordinator.RequestCompleted += OnRequestCompleted;
        _coordinator.RequestFailed += OnRequestCompleted;
        SettingsHost.SettingChanged += OnSettingChanged;

        foreach (var request in _coordinator.PendingRequests)
        {
            RegisterRequest(request);
        }

        EnsureVisible();
        ApplyAutoHideSettings();
        UpdateGhostMode();
    }

    public void Show()
    {
        ThrowIfDisposed();
        EnsureVisible(forceShow: true);
    }

    public void Hide()
    {
        ThrowIfDisposed();
        Dispatch(() =>
        {
            if (_window.IsVisible)
            {
                _window.Hide();
            }
        });
    }

    public void TrackRequest(OptimisationRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        Dispatch(() =>
        {
            RegisterRequest(request);
            EnsureVisible();
            UpdateGhostMode();
        });
    }

    public void ResetHudLayout()
    {
        ThrowIfDisposed();

        Dispatch(() =>
        {
            _viewModel.ResetLayout();
            PositionWindow();
            EnsureVisible(forceShow: true);
        });
    }

    private void EnsureVisible(bool forceShow = false)
    {
        if (!SettingsHost.Get(SettingsRegistry.EnableFloatingResults))
        {
            if (_window.IsVisible)
            {
                _window.Hide();
            }

            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        PositionWindow();
        _window.BringToFront();
    }

    private void PositionWindow()
    {
        var placement = SettingsHost.Get(SettingsRegistry.FloatingHudPlacement);
        _window.MoveToPlacement(placement);
    }

    private void OnProgressChanged(object? sender, OptimisationProgressEventArgs e)
    {
        Dispatch(() =>
        {
            if (!EnsureViewModel(e.Progress.RequestId))
            {
                _logger.LogDebug("Progress event received for unknown request {RequestId}", e.Progress.RequestId);
                return;
            }

            if (_results.TryGetValue(e.Progress.RequestId, out var viewModel))
            {
                CancelDismissTimer(viewModel.RequestId);
                viewModel.UpdateProgress(e.Progress);
            }

            EnsureVisible();
        });
    }

    private void OnExternalFilesDropped(object? sender, IReadOnlyList<string> paths)
    {
        Dispatch(() => ProcessDroppedFiles(paths));
    }

    private void OnRequestCompleted(object? sender, OptimisationCompletedEventArgs e)
    {
        Dispatch(() =>
        {
            if (!EnsureViewModel(e.Result.RequestId))
            {
                _logger.LogDebug("Completion event received for unknown request {RequestId}", e.Result.RequestId);
                return;
            }

            if (_results.TryGetValue(e.Result.RequestId, out var viewModel))
            {
                _requests.TryGetValue(e.Result.RequestId, out var request);
                viewModel.ApplyResult(e.Result, request);
                ScheduleAutoDismiss(viewModel);
            }

            EnsureVisible();
        });
    }

    private bool EnsureViewModel(string requestId)
    {
        if (_results.ContainsKey(requestId))
        {
            return true;
        }

        var request = FindRequest(requestId);
        if (request is null)
        {
            return false;
        }

        RegisterRequest(request);
        return true;
    }

    private void RegisterRequest(OptimisationRequest request)
    {
        CancelDismissTimer(request.RequestId);

        if (_results.TryGetValue(request.RequestId, out var existing))
        {
            _requests[request.RequestId] = request;
            if (existing.SourcePath is null)
            {
                existing.ApplyRequest(request);
            }
            return;
        }

        var viewModel = new FloatingResultViewModel(request.RequestId, DismissResult);
        viewModel.ApplyRequest(request);
        _results[request.RequestId] = viewModel;
        _requests[request.RequestId] = request;
        _viewModel.InsertResult(viewModel);
        TrimOverflow();
        UpdateGhostMode();
    }

    private OptimisationRequest? FindRequest(string requestId)
    {
        if (_requests.TryGetValue(requestId, out var existing))
        {
            return existing;
        }

        var pending = _coordinator.PendingRequests
            .FirstOrDefault(r => string.Equals(r.RequestId, requestId, StringComparison.Ordinal));
        if (pending is not null)
        {
            _requests[requestId] = pending;
        }

        return pending;
    }

    private void DismissResult(FloatingResultViewModel viewModel)
    {
        Dispatch(() => BeginDismissal(viewModel));
    }

    private void BeginDismissal(FloatingResultViewModel viewModel, bool animate = true)
    {
        if (!animate)
        {
            RemoveResult(viewModel);
            return;
        }

        if (!_pendingDismissals.Add(viewModel.RequestId))
        {
            return;
        }

        var animated = _window.TryAnimateDismissal(viewModel, () =>
        {
            _pendingDismissals.Remove(viewModel.RequestId);
            RemoveResult(viewModel);
        });

        if (!animated)
        {
            _pendingDismissals.Remove(viewModel.RequestId);
            RemoveResult(viewModel);
        }
    }

    private void RemoveResult(FloatingResultViewModel viewModel)
    {
        CancelDismissTimer(viewModel.RequestId);
        _pendingDismissals.Remove(viewModel.RequestId);

        if (!_results.Remove(viewModel.RequestId))
        {
            return;
        }

        _requests.Remove(viewModel.RequestId);
        _viewModel.RemoveResult(viewModel);
        UpdateGhostMode();
    }

    private void TrimOverflow()
    {
        while (_viewModel.Results.Count > MaxVisibleResults)
        {
            var overflow = _viewModel.Results[^1];
            BeginDismissal(overflow, animate: false);
        }
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (string.Equals(e.Name, SettingsRegistry.EnableFloatingResults.Name, StringComparison.Ordinal))
        {
            var enabled = e.Value is bool flag && flag;
            Dispatch(() =>
            {
                if (!enabled)
                {
                    _window.Hide();
                }
                else
                {
                    EnsureVisible(forceShow: true);
                    UpdateGhostMode();
                }
            });

            return;
        }

        if (string.Equals(e.Name, SettingsRegistry.AutoHideFloatingResults.Name, StringComparison.Ordinal) ||
            string.Equals(e.Name, SettingsRegistry.AutoHideFloatingResultsAfter.Name, StringComparison.Ordinal))
        {
            Dispatch(ApplyAutoHideSettings);
            return;
        }

        if (string.Equals(e.Name, SettingsRegistry.FloatingHudPlacement.Name, StringComparison.Ordinal))
        {
            Dispatch(PositionWindow);
        }
    }

    private void ApplyAutoHideSettings()
    {
        if (!SettingsHost.Get(SettingsRegistry.AutoHideFloatingResults))
        {
            foreach (var delay in _dismissDelays.Values)
            {
                delay.Cancel();
                delay.Dispose();
            }

            _dismissDelays.Clear();
            return;
        }

        foreach (var result in _viewModel.Results)
        {
            if (!result.IsRunning)
            {
                ScheduleAutoDismiss(result);
            }
        }
    }

    private void ProcessDroppedFiles(IEnumerable<string> paths)
    {
        var enqueued = 0;

        foreach (var path in paths)
        {
            var request = CreateRequestForPath(path);
            if (request is null)
            {
                continue;
            }

            RegisterRequest(request);
            _ = _coordinator.Enqueue(request);
            enqueued++;
        }

        if (enqueued > 0)
        {
            EnsureVisible(forceShow: true);
            UpdateGhostMode();
        }
    }

    private OptimisationRequest? CreateRequestForPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            var filePath = FilePath.From(rawPath);
            var itemType = ResolveItemType(filePath);
            if (itemType is null)
            {
                _logger.LogInformation("Skipping dropped file {FilePath} because the format is unsupported.", rawPath);
                return null;
            }

            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["source"] = "hud-drop"
            };

            OutputBehaviourSettings.ApplyTo(metadata);

            return new OptimisationRequest(itemType.Value, filePath, metadata: metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue dropped file {FilePath}", rawPath);
            return null;
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

        return null;
    }

    private void OnDropOverlayVisibilityChanged(object? sender, bool isVisible)
    {
        _overlayVisible = isVisible;
        UpdateGhostMode();
    }

    private void ScheduleAutoDismiss(FloatingResultViewModel viewModel)
    {
        CancelDismissTimer(viewModel.RequestId);

        if (!SettingsHost.Get(SettingsRegistry.AutoHideFloatingResults))
        {
            return;
        }

        if (viewModel.IsRunning)
        {
            return;
        }

        var seconds = SettingsHost.Get(SettingsRegistry.AutoHideFloatingResultsAfter);
        if (seconds <= 0)
        {
            seconds = SettingsRegistry.AutoHideFloatingResultsAfter.DefaultValue;
        }

        var clampedSeconds = Math.Clamp(seconds, 1, 600);
        var cts = new CancellationTokenSource();
        _dismissDelays[viewModel.RequestId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(clampedSeconds), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cts.Dispose();
                return;
            }

            Dispatch(() =>
            {
                if (!_dismissDelays.Remove(viewModel.RequestId))
                {
                    cts.Dispose();
                    return;
                }

                cts.Dispose();
                BeginDismissal(viewModel);
            });
        });
    }

    private void CancelDismissTimer(string requestId)
    {
        if (_dismissDelays.Remove(requestId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void UpdateGhostMode()
    {
        var shouldGhost = !_viewModel.HasResults && !_overlayVisible;
        _window.SetGhostMode(shouldGhost);
    }

    private void Dispatch(Action action)
    {
        if (_window.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = _window.Dispatcher.InvokeAsync(action, DispatcherPriority.Render);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(FloatingHudController));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _coordinator.ProgressChanged -= OnProgressChanged;
        _coordinator.RequestCompleted -= OnRequestCompleted;
        _coordinator.RequestFailed -= OnRequestCompleted;
        SettingsHost.SettingChanged -= OnSettingChanged;
        _window.ExternalFilesDropped -= OnExternalFilesDropped;
        _window.DropOverlayVisibilityChanged -= OnDropOverlayVisibilityChanged;

        foreach (var delay in _dismissDelays.Values)
        {
            delay.Cancel();
            delay.Dispose();
        }

        _dismissDelays.Clear();
    }
}
