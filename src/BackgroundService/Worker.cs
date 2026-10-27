using Squish.BackgroundService.Automation;
using Squish.BackgroundService.Clipboard;
using Microsoft.Extensions.Logging;

namespace Squish.BackgroundService;

public class Worker : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ClipboardOptimisationService _clipboardService;
    private readonly DirectoryOptimisationService _directoryService;
    private readonly WorkdirCleanupService _workdirCleanupService;
    private readonly ShortcutsBridge _shortcutsBridge;
    private readonly CrossAppAutomationHost _crossAppHost;
    private readonly ILogger<Worker> _logger;

    public Worker(ClipboardOptimisationService clipboardService, DirectoryOptimisationService directoryService, WorkdirCleanupService workdirCleanupService, ShortcutsBridge shortcutsBridge, CrossAppAutomationHost crossAppHost, ILogger<Worker> logger)
    {
        _clipboardService = clipboardService;
        _directoryService = directoryService;
        _workdirCleanupService = workdirCleanupService;
        _shortcutsBridge = shortcutsBridge;
        _crossAppHost = crossAppHost;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting background automation services.");

        // Each service is supervised on its own: a crash restarts just that service (with backoff) and never
        // affects the others or tears the host down.
        await Task.WhenAll(
            Supervise("clipboard", _clipboardService.RunAsync, stoppingToken),
            Supervise("folder-watcher", _directoryService.RunAsync, stoppingToken),
            Supervise("workdir-cleanup", _workdirCleanupService.RunAsync, stoppingToken),
            Supervise("shortcuts-bridge", _shortcutsBridge.RunAsync, stoppingToken),
            Supervise("cross-app-host", _crossAppHost.RunAsync, stoppingToken));

        _logger.LogInformation("Background automation services stopped.");
    }

    private Task Supervise(string name, Func<CancellationToken, Task> run, CancellationToken stoppingToken) =>
        ServiceSupervisor.RunAsync(name, run, _logger, stoppingToken);
}
