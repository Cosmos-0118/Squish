using ClopWindows.BackgroundService.Automation;
using ClopWindows.BackgroundService.Clipboard;
using Microsoft.Extensions.Logging;

namespace ClopWindows.BackgroundService;

public class Worker : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ClipboardOptimisationService _clipboardService;
    private readonly DirectoryOptimisationService _directoryService;
    private readonly ShortcutsBridge _shortcutsBridge;
    private readonly CrossAppAutomationHost _crossAppHost;
    private readonly ILogger<Worker> _logger;

    public Worker(ClipboardOptimisationService clipboardService, DirectoryOptimisationService directoryService, ShortcutsBridge shortcutsBridge, CrossAppAutomationHost crossAppHost, ILogger<Worker> logger)
    {
        _clipboardService = clipboardService;
        _directoryService = directoryService;
        _shortcutsBridge = shortcutsBridge;
        _crossAppHost = crossAppHost;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting background automation services.");
        await Task.WhenAll(
            _clipboardService.RunAsync(stoppingToken),
            _directoryService.RunAsync(stoppingToken),
            _shortcutsBridge.RunAsync(stoppingToken),
            _crossAppHost.RunAsync(stoppingToken));
        _logger.LogInformation("Background automation services stopped.");
    }
}
