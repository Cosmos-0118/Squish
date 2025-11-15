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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClopWindows.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private SimpleFileLoggerProvider? _fileLoggerProvider;
    private TrayIconService? _trayIconService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SettingsHost.EnsureInitialized();
        ShortcutCatalog.Initialize();

        var logDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clop", "logs");
        System.IO.Directory.CreateDirectory(logDirectory);
        var logFilePath = System.IO.Path.Combine(logDirectory, $"app-{DateTime.UtcNow:yyyyMMdd}.log");
        _fileLoggerProvider = new SimpleFileLoggerProvider(logFilePath);

        SharedLogger.Sink = (level, message, context) =>
        {
            try
            {
                var contextText = context is null ? string.Empty : $" {context}";
                var line = $"{DateTimeOffset.UtcNow:O} [{level}] Shared: {message}{contextText}";
                _fileLoggerProvider?.WriteLine(line);
            }
            catch
            {
                // ignore logging failures
            }
        };

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddDebug();
                if (_fileLoggerProvider is not null)
                {
                    logging.AddProvider(_fileLoggerProvider);
                }
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
        hudController.Show();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        _trayIconService = _host.Services.GetRequiredService<TrayIconService>();
        _trayIconService.Initialize(mainWindow);
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        SharedLogger.Sink = null;

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

        _fileLoggerProvider?.Dispose();
        _fileLoggerProvider = null;

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
