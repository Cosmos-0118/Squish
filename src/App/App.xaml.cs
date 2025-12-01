using System;
using System.Collections.Generic;
using System.Windows;
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
    private IHost? _host;
    private IDisposable? _sharedLogScope;
    private TrayIconService? _trayIconService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
                services.AddSingleton<IOptimiser, VideoOptimiser>();
                services.AddSingleton<IOptimiser, PdfOptimiser>();

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
            })
            .Build();

        _host.Start();

        _ = _host.Services.GetRequiredService<ThemeManager>();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        var hudController = _host.Services.GetRequiredService<FloatingHudController>();
        hudController.Initialize();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        _trayIconService = _host.Services.GetRequiredService<TrayIconService>();
        _trayIconService.Initialize(mainWindow);
        mainWindow.Show();
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
}
