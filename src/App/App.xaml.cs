using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using ClopWindows.App.Infrastructure;
using ClopWindows.App.Services;
using ClopWindows.App.ViewModels;
using ClopWindows.App.Views.FloatingHud;
using ClopWindows.BackgroundService;
using ClopWindows.BackgroundService.Automation;
using ClopWindows.BackgroundService.Clipboard;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;
using ClopWindows.Core.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClopWindows.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "ClopWindows.App.SingleInstance";
    internal const string BackgroundLaunchArgument = "--background";

    private IHost? _host;
    private IDisposable? _sharedLogScope;
    private TrayIconService? _trayIconService;
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryAcquireSingleInstanceMutex())
        {
            System.Windows.MessageBox.Show(
                "Clop is already running in the system tray.",
                "Clop",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        base.OnStartup(e);
        var launchToTray = ShouldLaunchSilently(e.Args);
        if (launchToTray)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        SettingsHost.EnsureInitialized();
        ShortcutCatalog.Initialize();

        _sharedLogScope = SharedLogging.EnableSharedLogger("app");

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureLogging(logging =>
            {
                logging.AddDebug();
                logging.AddSharedFileLogger("app", LogLevel.Information);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IOptimiser, ImageOptimiser>();
                services.AddSingleton<IOptimiser>(_ => new VideoOptimiser(VideoOptimiserOptions.Default.WithHardwareOverride()));
                services.AddSingleton<IOptimiser, PdfOptimiser>();
                services.AddSingleton<IOptimiser, DocumentOptimiser>();

                services.AddSingleton(provider =>
                {
                    var optimisers = provider.GetRequiredService<IEnumerable<IOptimiser>>();
                    return new OptimisationCoordinator(optimisers, Math.Max(Environment.ProcessorCount / 2, 2));
                });

                services.AddSingleton<OptimisedFileRegistry>();
                services.AddSingleton<ClipboardMonitor>();
                services.AddSingleton<ClipboardOptimisationService>();
                services.AddSingleton<DirectoryOptimisationService>();
                services.AddSingleton<ShortcutsBridge>();
                services.AddSingleton<CrossAppAutomationHost>();
                services.AddSingleton<IFolderPicker, FolderPicker>();
                services.AddHostedService<Worker>();

                services.AddSingleton<FloatingHudViewModel>();
                services.AddSingleton<FloatingHudWindow>();
                services.AddSingleton<FloatingHudController>();
                services.AddSingleton<KeyboardShortcutService>();
                services.AddSingleton<OnboardingViewModel>();
                services.AddSingleton<CompareViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<TrayIconService>();
                services.AddSingleton<ThemeManager>();
                services.AddSingleton<StartupRegistrationService>();
            })
            .Build();

        _host.Start();

        _ = _host.Services.GetRequiredService<ThemeManager>();
        _ = _host.Services.GetRequiredService<StartupRegistrationService>();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        var hudController = _host.Services.GetRequiredService<FloatingHudController>();
        hudController.Initialize();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var shortcutService = _host.Services.GetRequiredService<KeyboardShortcutService>();
        MainWindow = mainWindow;
        _trayIconService = _host.Services.GetRequiredService<TrayIconService>();
        _trayIconService.Initialize(mainWindow);

        // Ensure the main window has a handle so global shortcuts can register even when launching to tray.
        var handleHelper = new WindowInteropHelper(mainWindow);
        handleHelper.EnsureHandle();
        shortcutService.Attach(mainWindow);

        if (!launchToTray)
        {
            mainWindow.Show();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            }
            finally
            {
                _host.Dispose();
            }
        }

        _trayIconService?.Dispose();
        _trayIconService = null;

        _sharedLogScope?.Dispose();
        _sharedLogScope = null;

        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled dispatcher exception", e.Exception);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception", e.ExceptionObject);
    }

    private bool TryAcquireSingleInstanceMutex()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);

        if (createdNew)
        {
            return true;
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
        return false;
    }

    private static bool ShouldLaunchSilently(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, BackgroundLaunchArgument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
