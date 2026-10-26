using System;
using System.Collections.Generic;
using Squish.BackgroundService;
using Squish.BackgroundService.Automation;
using Squish.BackgroundService.Clipboard;
using Squish.Core.Optimizers;
using Squish.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Squish.Core.Shared;
using Squish.Core.Shared.Logging;

LegacyDataMigration.EnsureMigrated();
using var sharedLogging = SharedLogging.EnableSharedLogger("background-service");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSharedFileLogger("background-service", LogLevel.Information);

builder.Services.AddSingleton<IOptimiser>(_ => new SettingsBackedImageOptimiser(ImageOptimiserOptionsFactory.FromSettings));
builder.Services.AddSingleton<IOptimiser>(_ => new VideoOptimiser(VideoOptimiserOptions.Default.WithHardwareOverride()));

builder.Services.AddSingleton(provider =>
{
    var optimisers = provider.GetRequiredService<IEnumerable<IOptimiser>>();
    return new OptimisationCoordinator(optimisers, degreeOfParallelism: Math.Max(Environment.ProcessorCount / 2, 2));
});

builder.Services.AddSingleton<OptimisedFileRegistry>();
builder.Services.AddSingleton<ClipboardMonitor>();
builder.Services.AddSingleton<ClipboardOptimisationService>();
builder.Services.AddSingleton<DirectoryOptimisationService>();
builder.Services.AddSingleton<WorkdirCleanupService>();
builder.Services.AddSingleton<ShortcutsBridge>();
builder.Services.AddSingleton<CrossAppAutomationHost>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
