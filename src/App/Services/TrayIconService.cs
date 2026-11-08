using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Resources;
using Squish.App.Localization;
using Squish.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Squish.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const string TrayIconPackUri = "pack://application:,,,/Squish;component/Assets/Brand/Squish.ico";

    private readonly ILogger<TrayIconService> _logger;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _contextMenu;
    private readonly Icon _trayIcon;
    private readonly MainWindowManager _mainWindowManager;
    private bool _initialized;
    private bool _exitRequested;
    private bool _disposed;
    private bool _balloonShown;
    private bool _runInBackground;

    public TrayIconService(MainWindowManager mainWindowManager, ILogger<TrayIconService> logger)
    {
        _mainWindowManager = mainWindowManager;
        _logger = logger;

        _contextMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayIcon = LoadTrayIconOrDefault();
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = SquishStringCatalog.Get("tray.tooltip"),
            Icon = _trayIcon,
            Visible = false,
            ContextMenuStrip = _contextMenu
        };

        var openItem = new System.Windows.Forms.ToolStripMenuItem(SquishStringCatalog.Get("tray.open"), null, (_, _) => ShowMainWindow());
        var exitItem = new System.Windows.Forms.ToolStripMenuItem(SquishStringCatalog.Get("tray.exit"), null, (_, _) => ExitApplication());

        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        _runInBackground = SettingsHost.Get(SettingsRegistry.RunInBackground);
        SettingsHost.SettingChanged += OnSettingChanged;
    }

    /// <summary>Shows the tray icon. The main window is not needed: it is created on demand by <see cref="MainWindowManager"/>.</summary>
    public void Initialize()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrayIconService));
        }

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _mainWindowManager.ClosedToBackground += OnMainWindowClosedToBackground;

        UpdateTrayVisibility();
    }

    public void ShowMainWindow() => _mainWindowManager.ShowOnDispatcher();

    /// <summary>The window used to just hide; now it is destroyed, but the user still gets the one-time "still running" hint.</summary>
    private void OnMainWindowClosedToBackground(object? sender, EventArgs e)
    {
        if (_balloonShown || !_runInBackground || _disposed)
        {
            return;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = SquishStringCatalog.Get("tray.balloon.title");
            _notifyIcon.BalloonTipText = SquishStringCatalog.Get("tray.balloon.message");
            _notifyIcon.ShowBalloonTip(2000);
            _balloonShown = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show the tray balloon.");
        }
    }

    private void ExitApplication()
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        _exitRequested = true;
        _mainWindowManager.NotifyExiting();
        _notifyIcon.Visible = false;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                System.Windows.Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to shut down application from tray.");
            }
        }));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SettingsHost.SettingChanged -= OnSettingChanged;
        _mainWindowManager.ClosedToBackground -= OnMainWindowClosedToBackground;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _trayIcon.Dispose();
    }

    private void UpdateTrayVisibility()
    {
        var shouldShow = _runInBackground && !_disposed && _initialized;
        if (_notifyIcon.Visible == shouldShow)
        {
            return;
        }

        _notifyIcon.Visible = shouldShow;
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Name != SettingsRegistry.RunInBackground.Name)
        {
            return;
        }

        var desired = e.Value is bool flag ? flag : SettingsHost.Get(SettingsRegistry.RunInBackground);
        _runInBackground = desired;
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTrayVisibility();
            if (!_runInBackground)
            {
                ShowMainWindow();
            }
        }));
    }

    private Icon LoadTrayIconOrDefault()
    {
        try
        {
            var resourceUri = new Uri(TrayIconPackUri, UriKind.RelativeOrAbsolute);
            StreamResourceInfo? resource = System.Windows.Application.GetResourceStream(resourceUri);
            if (resource?.Stream is null)
            {
                return (Icon)SystemIcons.Application.Clone();
            }

            using var iconStream = resource.Stream;
            using var icon = new Icon(iconStream);
            return (Icon)icon.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tray icon from {PackUri}", TrayIconPackUri);
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
