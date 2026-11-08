using System;
using System.ComponentModel;
using System.Windows;
using Squish.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Squish.App.Services;

/// <summary>
/// Owns the main window's lifetime. The window (and WPF's render target / Direct3D driver with it) is only created
/// when someone asks for it and is really closed afterwards; the tray icon and global hotkeys keep the app alive.
/// </summary>
public sealed class MainWindowManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MainWindowManager> _logger;
    private MainWindow? _window;
    private bool _exiting;

    public MainWindowManager(IServiceProvider serviceProvider, ILogger<MainWindowManager> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>Raised on the UI thread after the window is closed while the app keeps running in the tray.</summary>
    public event EventHandler? ClosedToBackground;

    public bool IsOpen => _window is not null;

    /// <summary>Marks the app as shutting down so closing the window does not try to apply the close-to-tray rules.</summary>
    public void NotifyExiting() => _exiting = true;

    /// <summary>Thread-safe: shows (creating if necessary) and activates the main window.</summary>
    public void ShowOnDispatcher()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            Show();
        }
        else
        {
            _ = dispatcher.BeginInvoke(new Action(Show));
        }
    }

    /// <summary>Shows and activates the window. UI thread only.</summary>
    public void Show()
    {
        try
        {
            if (_exiting)
            {
                return;
            }

            var window = _window ??= CreateWindow();
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show the main window.");
        }
    }

    private MainWindow CreateWindow()
    {
        var window = _serviceProvider.GetRequiredService<MainWindow>();
        window.Closing += OnClosing;
        window.StateChanged += OnStateChanged;
        window.IsVisibleChanged += OnIsVisibleChanged;
        window.Closed += OnClosed;

        // Dialogs use Application.MainWindow as their owner; with no startup window it has to be set explicitly.
        System.Windows.Application.Current.MainWindow = window;
        return window;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => IdleTrim.MainWindowVisible = _window?.IsVisible == true;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Minimising used to hide the window to the tray; with the window destroyed on close, do the same.
        if (_exiting || sender is not MainWindow window || window.WindowState != WindowState.Minimized)
        {
            return;
        }

        if (SettingsHost.Get(SettingsRegistry.RunInBackground))
        {
            window.Close();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        if (!SettingsHost.Get(SettingsRegistry.RunInBackground))
        {
            // Without a tray icon, closing the window is quitting the app.
            _exiting = true;
            _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    System.Windows.Application.Current?.Shutdown();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to shut down application when closing window.");
                }
            }));
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Closing -= OnClosing;
            window.StateChanged -= OnStateChanged;
            window.IsVisibleChanged -= OnIsVisibleChanged;
            window.Closed -= OnClosed;
            if (ReferenceEquals(System.Windows.Application.Current?.MainWindow, window))
            {
                System.Windows.Application.Current.MainWindow = null;
            }
        }

        _window = null;
        IdleTrim.MainWindowVisible = false;

        if (!_exiting)
        {
            ClosedToBackground?.Invoke(this, EventArgs.Empty);
            IdleTrim.Schedule();
        }
    }
}
