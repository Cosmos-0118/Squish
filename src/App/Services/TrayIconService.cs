using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using ClopWindows.App.Localization;
using Microsoft.Extensions.Logging;

namespace ClopWindows.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly ILogger<TrayIconService> _logger;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _contextMenu;
    private MainWindow? _mainWindow;
    private bool _exitRequested;
    private bool _disposed;
    private bool _balloonShown;

    public TrayIconService(ILogger<TrayIconService> logger)
    {
        _logger = logger;

        _contextMenu = new System.Windows.Forms.ContextMenuStrip();
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = ClopStringCatalog.Get("tray.tooltip"),
            Icon = SystemIcons.Application,
            Visible = false,
            ContextMenuStrip = _contextMenu
        };

        var openItem = new System.Windows.Forms.ToolStripMenuItem(ClopStringCatalog.Get("tray.open"), null, (_, _) => ShowMainWindow());
        var exitItem = new System.Windows.Forms.ToolStripMenuItem(ClopStringCatalog.Get("tray.exit"), null, (_, _) => ExitApplication());

        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    public void Initialize(MainWindow mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TrayIconService));
        }

        if (_mainWindow is not null)
        {
            return;
        }

        _mainWindow = mainWindow;
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.StateChanged += OnMainWindowStateChanged;

        _notifyIcon.Visible = true;
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_mainWindow.Visibility != Visibility.Visible)
            {
                _mainWindow.Show();
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
        }));
    }

    private void HideToTray()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_mainWindow.WindowState != WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Minimized;
                }

                _mainWindow.Hide();

                if (!_balloonShown)
                {
                    _notifyIcon.BalloonTipTitle = ClopStringCatalog.Get("tray.balloon.title");
                    _notifyIcon.BalloonTipText = ClopStringCatalog.Get("tray.balloon.message");
                    _notifyIcon.ShowBalloonTip(2000);
                    _balloonShown = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to hide main window to tray.");
            }
        }));
    }

    private void ExitApplication()
    {
        if (_exitRequested || _disposed)
        {
            return;
        }

        _exitRequested = true;
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

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.WindowState == WindowState.Minimized && !_exitRequested)
        {
            HideToTray();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.StateChanged -= OnMainWindowStateChanged;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
