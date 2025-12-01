using System;
using System.Collections.Generic;
using ClopWindows.BackgroundService;
using ClopWindows.BackgroundService.Automation;
using ClopWindows.BackgroundService.Clipboard;
using ClopWindows.Core.Optimizers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IOptimiser, ImageOptimiser>();
builder.Services.AddSingleton<IOptimiser, VideoOptimiser>();
builder.Services.AddSingleton<IOptimiser, PdfOptimiser>();

builder.Services.AddSingleton(provider =>
{
    var optimisers = provider.GetRequiredService<IEnumerable<IOptimiser>>();
    return new OptimisationCoordinator(optimisers, degreeOfParallelism: Math.Max(Environment.ProcessorCount / 2, 2));
});

builder.Services.AddSingleton<OptimisedFileRegistry>();
builder.Services.AddSingleton<ClipboardMonitor>();
builder.Services.AddSingleton<ClipboardOptimisationService>();
builder.Services.AddSingleton<DirectoryOptimisationService>();
builder.Services.AddSingleton<ShortcutsBridge>();
builder.Services.AddSingleton<CrossAppAutomationHost>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
