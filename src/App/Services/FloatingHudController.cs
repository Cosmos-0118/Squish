using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ClopWindows.App.ViewModels;
using ClopWindows.App.Views.FloatingHud;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Settings;
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

        _coordinator.ProgressChanged += OnProgressChanged;
        _coordinator.RequestCompleted += OnRequestCompleted;
        _coordinator.RequestFailed += OnRequestCompleted;
        SettingsHost.SettingChanged += OnSettingChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _window.LocationChanged += OnWindowLocationChanged;

        foreach (var request in _coordinator.PendingRequests)
        {
            RegisterRequest(request);
        }

        EnsureVisible();
        ApplyAutoHideSettings();
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
        });
    }

    public void ResetHudLayout()
    {
        ThrowIfDisposed();

        Dispatch(() =>
        {
            _viewModel.ResetLayout();
            ClearPinnedLocation();
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

        if (!_viewModel.HasResults && !forceShow)
        {
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
        if (_viewModel.IsPinned)
        {
            if (!TryApplyPinnedPosition())
            {
                PersistPinnedLocation();
            }

            return;
        }

        _window.MoveToTopRight();
    }

    private bool TryApplyPinnedPosition()
    {
        var left = SettingsHost.Get(SettingsRegistry.FloatingHudPinnedLeft);
        var top = SettingsHost.Get(SettingsRegistry.FloatingHudPinnedTop);
        if (double.IsNaN(left) || double.IsNaN(top))
        {
            return false;
        }

        if (Math.Abs(_window.Left - left) < 0.5 && Math.Abs(_window.Top - top) < 0.5)
        {
            return true;
        }

        _window.MoveTo(left, top);
        return true;
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

        if (_viewModel.HasResults)
        {
            return;
        }

        if (_window.IsVisible)
        {
            _window.Hide();
        }
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

        if (string.Equals(e.Name, SettingsRegistry.FloatingHudPinned.Name, StringComparison.Ordinal) ||
            string.Equals(e.Name, SettingsRegistry.FloatingHudPinnedLeft.Name, StringComparison.Ordinal) ||
            string.Equals(e.Name, SettingsRegistry.FloatingHudPinnedTop.Name, StringComparison.Ordinal))
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

    private void PersistPinnedLocation()
    {
        if (!_viewModel.IsPinned)
        {
            return;
        }

        var left = _window.Left;
        var top = _window.Top;
        if (double.IsNaN(left) || double.IsNaN(top))
        {
            return;
        }

        var currentLeft = SettingsHost.Get(SettingsRegistry.FloatingHudPinnedLeft);
        var currentTop = SettingsHost.Get(SettingsRegistry.FloatingHudPinnedTop);

        if (double.IsNaN(currentLeft) || Math.Abs(currentLeft - left) > 0.5)
        {
            SettingsHost.Set(SettingsRegistry.FloatingHudPinnedLeft, left);
        }

        if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.5)
        {
            SettingsHost.Set(SettingsRegistry.FloatingHudPinnedTop, top);
        }
    }

    private static void ClearPinnedLocation()
    {
        SettingsHost.Set(SettingsRegistry.FloatingHudPinnedLeft, double.NaN);
        SettingsHost.Set(SettingsRegistry.FloatingHudPinnedTop, double.NaN);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(FloatingHudViewModel.IsPinned), StringComparison.Ordinal))
        {
            return;
        }

        Dispatch(() =>
        {
            if (_viewModel.IsPinned)
            {
                PersistPinnedLocation();
            }
            else
            {
                ClearPinnedLocation();
                PositionWindow();
            }
        });
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (_viewModel.IsPinned)
        {
            PersistPinnedLocation();
        }
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _window.LocationChanged -= OnWindowLocationChanged;

        foreach (var delay in _dismissDelays.Values)
        {
            delay.Cancel();
            delay.Dispose();
        }

        _dismissDelays.Clear();
    }
}
