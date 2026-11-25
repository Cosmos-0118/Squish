using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Squish.BackgroundService.Automation;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Squish.Core.Documents;
using Squish.Core.Optimizers;
using Squish.Core.Pdf;
using Squish.Core.Settings;
using Squish.Core.Shared;

namespace Squish.CliBridge;

internal static class WatchCommandBuilder
{
    public static Command Create()
    {
        var command = new Command("watch", "Monitor directories and optimise files as they change.");
        var paths = new Argument<List<string>>("paths")
        {
            Description = "Directories or files to watch for optimisations.",
            DefaultValueFactory = _ => new List<string>(),
            Arity = ArgumentArity.ZeroOrMore
        };
        command.Arguments.Add(paths);

        var recursive = new Option<bool>("--recursive", "-r") { Description = "Watch subdirectories recursively." };
        var skipErrors = new Option<bool>("--skip-errors", "-s") { Description = "Skip missing paths instead of failing." };
        var aggressive = new Option<bool>("--aggressive", "-a") { Description = "Use aggressive optimisation presets." };
        var removeAudio = new Option<bool>("--remove-audio") { DefaultValueFactory = _ => false, Description = "Strip audio tracks from videos." };
        var json = new Option<bool>("--json", "-j") { Description = "Emit optimisation results as JSON lines." };
        var noProgress = new Option<bool>("--no-progress", "-n") { Description = "Suppress in-flight progress updates." };
        var playbackSpeed = new Option<double?>("--playback-speed-factor") { Description = "Speed up or slow down videos (1.0 means no change)." };
        var types = new Option<string[]>("--types")
        {
            DefaultValueFactory = _ => Array.Empty<string>(),
            Description = "File types/extensions to include.",
            AllowMultipleArgumentsPerToken = true
        };
        var excludeTypes = new Option<string[]>("--exclude-types")
        {
            DefaultValueFactory = _ => Array.Empty<string>(),
            Description = "File types/extensions to exclude.",
            AllowMultipleArgumentsPerToken = true
        };
        var debounce = new Option<int>("--debounce-ms") { DefaultValueFactory = _ => 400, Description = "Suppress duplicate watcher events for this many milliseconds." };
        var readyTimeout = new Option<int>("--ready-timeout-ms") { DefaultValueFactory = _ => 8000, Description = "Maximum time to wait for files to stop changing before optimising." };

        command.Options.Add(recursive);
        command.Options.Add(skipErrors);
        command.Options.Add(aggressive);
        command.Options.Add(removeAudio);
        command.Options.Add(json);
        command.Options.Add(noProgress);
        command.Options.Add(playbackSpeed);
        command.Options.Add(types);
        command.Options.Add(excludeTypes);
        command.Options.Add(debounce);
        command.Options.Add(readyTimeout);

        command.SetAction(async (parse, cancellationToken) =>
        {
            var options = new WatchCommandOptions
            {
                Paths = parse.GetValue(paths) ?? new List<string>(),
                Recursive = parse.GetValue(recursive),
                SkipErrors = parse.GetValue(skipErrors),
                Aggressive = parse.GetValue(aggressive),
                RemoveAudio = parse.GetValue(removeAudio),
                Json = parse.GetValue(json),
                ShowProgress = !parse.GetValue(noProgress),
                PlaybackSpeedFactor = parse.GetValue(playbackSpeed),
                IncludeTypes = parse.GetValue(types) ?? Array.Empty<string>(),
                ExcludeTypes = parse.GetValue(excludeTypes) ?? Array.Empty<string>(),
                DebounceMilliseconds = Math.Clamp(parse.GetValue(debounce), 50, 10000),
                ReadyTimeoutMilliseconds = Math.Clamp(parse.GetValue(readyTimeout), 500, 60000)
            };

            var handler = new WatchCommandHandler(options, cancellationToken);
            return await handler.ExecuteAsync().ConfigureAwait(false);
        });

        return command;
    }
}

internal sealed record WatchCommandOptions
{
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    public bool Recursive { get; init; }
    public bool SkipErrors { get; init; }
    public bool Aggressive { get; init; }
    public bool RemoveAudio { get; init; }
    public bool Json { get; init; }
    public bool ShowProgress { get; init; } = true;
    public double? PlaybackSpeedFactor { get; init; }
    public IReadOnlyList<string> IncludeTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeTypes { get; init; } = Array.Empty<string>();
    public int DebounceMilliseconds { get; init; } = 400;
    public int ReadyTimeoutMilliseconds { get; init; } = 8000;
}

internal sealed class WatchCommandHandler
{
    private readonly WatchCommandOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly TypeFilter _filter;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<string, TargetFile> _targets = new(StringComparer.Ordinal);

    public WatchCommandHandler(WatchCommandOptions options, CancellationToken cancellationToken)
    {
        _options = options;
        _cancellationToken = cancellationToken;
        _filter = new TypeFilter(options.IncludeTypes, options.ExcludeTypes);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = options.Json
        };
    }

    public async Task<int> ExecuteAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            Console.Error.WriteLine("[error] squish watch requires Windows 7 or later.");
            return 1;
        }

        var directories = ResolveDirectories();
        if (directories.Count == 0)
        {
            Console.Error.WriteLine("[error] No accessible directories to watch.");
            return 1;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        var token = linkedCts.Token;
        ConsoleCancelEventHandler? cancelHandler = null;
        if (!Console.IsInputRedirected)
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                linkedCts.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
        }

        var imageOptions = ImageOptimiserOptionsFactory.FromSettings();
        var videoOptions = (VideoOptimiserOptionsFactory.FromSettings() with
        {
            AggressiveQuality = _options.Aggressive,
            // The CLI flag can only add to the saved setting, never switch audio removal off.
            RemoveAudio = _options.RemoveAudio || SettingsHost.Get(SettingsRegistry.RemoveAudioFromVideos)
        }).WithHardwareOverride();

        await using var coordinator = new OptimisationCoordinator(
            new IOptimiser[]
            {
                new ImageOptimiser(imageOptions),
                new VideoOptimiser(videoOptions),
                new PdfOptimiser(PdfOptimiserOptionsFactory.FromSettings()),
                new DocumentOptimiser(DocumentOptimiserOptionsFactory.FromSettings())
            },
            Math.Max(1, Environment.ProcessorCount / 2));

        // The same watcher the background service uses: settle queue, share-mode readiness probe, markers, output
        // suppression and reconciliation scans.
        await using var service = new DirectoryOptimisationService(
            coordinator,
            new OptimisedFileRegistry(),
            new WatchLogger(_options.Json),
            new CliFolderWatchSettings(_options, directories, _filter),
            BuildWatchOptions(),
            TimeProvider.System);

        var progressPrinter = new ProgressPrinter(_options.ShowProgress);
        EventHandler<OptimisationProgressEventArgs> progressHandler = (_, e) =>
        {
            if (_targets.TryGetValue(e.Progress.RequestId, out var target))
            {
                progressPrinter.Report(target, e.Progress);
            }
        };

        coordinator.ProgressChanged += progressHandler;
        service.JobSubmitted += OnJobSubmitted;
        service.JobCompleted += OnJobCompleted;

        if (!_options.Json)
        {
            Console.WriteLine($"[info] Watching {directories.Count} location(s). Press Ctrl+C to stop.");
        }

        try
        {
            await service.RunAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown requested
        }
        finally
        {
            coordinator.ProgressChanged -= progressHandler;
            service.JobSubmitted -= OnJobSubmitted;
            service.JobCompleted -= OnJobCompleted;

            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        return 0;
    }

    private FolderWatchOptions BuildWatchOptions()
    {
        var debounce = TimeSpan.FromMilliseconds(_options.DebounceMilliseconds);
        var defaults = FolderWatchOptions.Default;
        return defaults with
        {
            ImageQuietPeriod = debounce,
            VideoQuietPeriod = debounce > defaults.VideoQuietPeriod ? debounce : defaults.VideoQuietPeriod,
            DocumentQuietPeriod = debounce > defaults.DocumentQuietPeriod ? debounce : defaults.DocumentQuietPeriod,
            MaxPendingAge = TimeSpan.FromMilliseconds(_options.ReadyTimeoutMilliseconds)
        };
    }

    private void OnJobSubmitted(object? sender, DirectoryJob job)
    {
        _targets[job.RequestId] = new TargetFile(job.Path, job.Type, job.Length);
    }

    private void OnJobCompleted(object? sender, DirectoryJobCompleted e)
    {
        _targets.TryRemove(e.Job.RequestId, out _);
        var result = e.Result;
        if (result is null)
        {
            return;
        }

        var target = new TargetFile(e.Job.Path, e.Job.Type, e.Job.Length);
        if (_options.Json)
        {
            var payload = new
            {
                requestId = result.RequestId,
                status = result.Status.ToString().ToLowerInvariant(),
                sourcePath = target.Path.Value,
                outputPath = result.OutputPath?.Value,
                message = result.Message
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, _jsonOptions));
            return;
        }

        if (result.Status == OptimisationStatus.Succeeded)
        {
            Console.WriteLine(CliSuccess.FromResult(result, target).FormatForConsole());
        }
        else
        {
            Console.Error.WriteLine(CliFailure.FromResult(result, target).FormatForConsole());
        }
    }

    private IReadOnlyList<string> ResolveDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_options.Paths.Count == 0)
        {
            directories.Add(Environment.CurrentDirectory);
        }

        foreach (var candidate in _options.Paths)
        {
            var expanded = ExpandPath(candidate);
            if (Directory.Exists(expanded))
            {
                directories.Add(expanded);
            }
            else if (File.Exists(expanded))
            {
                directories.Add(Path.GetDirectoryName(expanded)!);
            }
            else if (!_options.SkipErrors)
            {
                throw new DirectoryNotFoundException($"Path '{candidate}' was not found.");
            }
            else if (!_options.Json)
            {
                Console.Error.WriteLine($"[warn] Skipping missing path '{candidate}'.");
            }
        }

        return directories.ToList();
    }

    private static string ExpandPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var value = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        if (value.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = Path.Combine(home, value[2..]);
        }

        return Path.GetFullPath(value);
    }
}

/// <summary>Feeds the CLI's flags to the shared folder watcher in place of the app's saved settings.</summary>
internal sealed class CliFolderWatchSettings : IFolderWatchSettings
{
    private readonly WatchCommandOptions _options;

    public CliFolderWatchSettings(WatchCommandOptions options, IReadOnlyList<string> directories, TypeFilter filter)
    {
        _options = options;
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Current = new FolderWatchConfig(
            Paused: false,
            AutoImages: true,
            AutoVideos: true,
            ImageDirectories: directories,
            VideoDirectories: directories,
            SkipImageFormats: none,
            SkipVideoFormats: none,
            MaxImageSizeMb: 0,
            MaxVideoSizeMb: 0,
            MaxImageJobs: 0,
            MaxVideoJobs: 0,
            AutoPdfs: true,
            AutoDocuments: true,
            PdfDirectories: directories,
            DocumentDirectories: directories,
            MaxDocumentSizeMb: 0,
            MaxDocumentJobs: 0,
            Recursive: options.Recursive,
            ExtensionFilter: extension => filter.Allows(extension ?? string.Empty));
    }

    public FolderWatchConfig Current { get; }

    // The CLI's settings are fixed for the lifetime of the command.
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public void ApplyRequestMetadata(ItemType type, FilePath path, IDictionary<string, object?> metadata)
    {
        metadata["source"] = "cli.watch";
        metadata["watch.recursive"] = _options.Recursive;
        metadata["watch.debounceMs"] = _options.DebounceMilliseconds;

        if (_options.Aggressive)
        {
            metadata["watch.aggressive"] = true;
            metadata[OptimisationMetadata.ImageAggressive] = true;
            metadata[OptimisationMetadata.VideoAggressive] = true;
            metadata[OptimisationMetadata.PdfAggressive] = true;
            metadata[OptimisationMetadata.DocumentAggressive] = true;
        }

        if (_options.RemoveAudio)
        {
            metadata["watch.removeAudio"] = true;
            metadata["video.removeAudio"] = true;
        }

        if (_options.PlaybackSpeedFactor.HasValue)
        {
            metadata["watch.playbackSpeed"] = _options.PlaybackSpeedFactor.Value;
            metadata["video.playbackSpeed"] = _options.PlaybackSpeedFactor.Value;
        }

        OutputBehaviourSettings.ApplyTo(metadata);
        VideoEncoderPresetSettings.ApplyTo(metadata);
    }
}

/// <summary>Surfaces watcher warnings and errors on stderr (the service logs everything else at lower levels).</summary>
internal sealed class WatchLogger : ILogger<DirectoryOptimisationService>
{
    private readonly bool _quiet;

    public WatchLogger(bool quiet) => _quiet = quiet;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => !_quiet && logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Console.Error.WriteLine($"[{(logLevel >= LogLevel.Error ? "error" : "warn")}] {formatter(state, exception)}");
        }
    }
}
