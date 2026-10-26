using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Squish.Core.Optimizers;
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
    private readonly ConcurrentDictionary<string, WatchRequestContext> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new(StringComparer.OrdinalIgnoreCase);

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

        var channel = Channel.CreateUnbounded<WatchFileEvent>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });

        var watchers = new List<FileSystemWatcher>();
        foreach (var directory in directories)
        {
            watchers.Add(CreateWatcher(directory, channel.Writer));
        }

        var imageOptions = ImageOptimiserOptionsFactory.FromSettings();
        var videoOptions = (VideoOptimiserOptions.Default with
        {
            AggressiveQuality = _options.Aggressive,
            RemoveAudio = _options.RemoveAudio
        }).WithHardwareOverride();
        var pdfOptions = PdfOptimiserOptions.Default with { AggressiveByDefault = _options.Aggressive };
        var pdfOptimiser = new PdfOptimiser(pdfOptions);
        var documentOptions = DocumentConversionOptions.Default;

        await using var coordinator = new OptimisationCoordinator(
            new IOptimiser[]
            {
                new ImageOptimiser(imageOptions),
                new VideoOptimiser(videoOptions),
                pdfOptimiser,
                new DocumentOptimiser(documentOptions, pdfOptimiser: pdfOptimiser)
            },
            Math.Max(1, Environment.ProcessorCount / 2));

        var progressPrinter = new ProgressPrinter(_options.ShowProgress);
        EventHandler<OptimisationProgressEventArgs>? progressHandler = null;
        progressHandler = (_, e) =>
        {
            if (_requests.TryGetValue(e.Progress.RequestId, out var ctx))
            {
                progressPrinter.Report(ctx.Target, e.Progress);
            }
        };

        if (progressHandler is not null)
        {
            coordinator.ProgressChanged += progressHandler;
        }
        coordinator.RequestCompleted += OnCoordinatorCompleted;
        coordinator.RequestFailed += OnCoordinatorCompleted;

        var processingTask = Task.Run(() => ProcessEventsAsync(channel.Reader, coordinator, token), token);

        if (!_options.Json)
        {
            Console.WriteLine($"[info] Watching {directories.Count} location(s). Press Ctrl+C to stop.");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown requested
        }
        finally
        {
            channel.Writer.TryComplete();
            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            if (progressHandler is not null)
            {
                coordinator.ProgressChanged -= progressHandler;
            }
            coordinator.RequestCompleted -= OnCoordinatorCompleted;
            coordinator.RequestFailed -= OnCoordinatorCompleted;

            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        return 0;
    }

    private async Task ProcessEventsAsync(ChannelReader<WatchFileEvent> reader, OptimisationCoordinator coordinator, CancellationToken token)
    {
        var debounceWindow = TimeSpan.FromMilliseconds(_options.DebounceMilliseconds);

        await foreach (var watchEvent in reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();

            var path = watchEvent.Path.Value;
            if (!File.Exists(path))
            {
                if (!_options.SkipErrors && !_options.Json)
                {
                    Console.Error.WriteLine($"[warn] Skipping missing file '{path}'.");
                }
                continue;
            }

            if (_recent.TryGetValue(path, out var last) && (watchEvent.ObservedAt - last) < debounceWindow)
            {
                continue;
            }

            _recent[path] = watchEvent.ObservedAt;

            if (!_filter.Allows(watchEvent.Path.Extension ?? string.Empty))
            {
                continue;
            }

            if (IsWithinWorkRoot(watchEvent.Path))
            {
                continue;
            }

            if (!_inFlight.TryAdd(path, 0))
            {
                continue;
            }

            var enqueued = false;
            try
            {
                var target = await CreateTargetAsync(watchEvent.Path, token).ConfigureAwait(false);
                if (target is not TargetFile targetValue)
                {
                    continue;
                }

                var metadata = BuildMetadata(watchEvent);
                var request = new OptimisationRequest(targetValue.ItemType, targetValue.Path, metadata: metadata);
                var context = new WatchRequestContext(targetValue, path, watchEvent.ObservedAt);
                if (!_requests.TryAdd(request.RequestId, context))
                {
                    continue;
                }

                coordinator.Enqueue(request, token);
                enqueued = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!_options.Json)
                {
                    Console.Error.WriteLine($"[warn] Failed to schedule '{path}': {ex.Message}");
                }
            }
            finally
            {
                if (!enqueued)
                {
                    _inFlight.TryRemove(path, out _);
                }
            }
        }
    }

    private void OnCoordinatorCompleted(object? sender, OptimisationCompletedEventArgs e)
    {
        if (!_requests.TryRemove(e.Result.RequestId, out var context))
        {
            return;
        }

        _inFlight.TryRemove(context.SourcePath, out _);
        _recent[context.SourcePath] = DateTimeOffset.UtcNow;

        if (_options.Json)
        {
            var payload = new
            {
                requestId = e.Result.RequestId,
                status = e.Result.Status.ToString().ToLowerInvariant(),
                sourcePath = context.Target.Path.Value,
                outputPath = e.Result.OutputPath?.Value,
                message = e.Result.Message
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, _jsonOptions));
            return;
        }

        if (e.Result.Status == OptimisationStatus.Succeeded)
        {
            var success = CliSuccess.FromResult(e.Result, context.Target);
            Console.WriteLine(success.FormatForConsole());
        }
        else
        {
            var failure = CliFailure.FromResult(e.Result, context.Target);
            Console.Error.WriteLine(failure.FormatForConsole());
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

    private FileSystemWatcher CreateWatcher(string directory, ChannelWriter<WatchFileEvent> writer)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = _options.Recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler handler = (_, e) => QueuePath(e.FullPath, writer);
        RenamedEventHandler renameHandler = (_, e) => QueuePath(e.FullPath, writer);

        watcher.Created += handler;
        watcher.Changed += handler;
        watcher.Renamed += renameHandler;
        watcher.Error += (_, ex) =>
        {
            if (!_options.Json)
            {
                Console.Error.WriteLine($"[warn] FileSystemWatcher error: {ex.GetException().Message}");
            }
        };

        return watcher;
    }

    private static void QueuePath(string? path, ChannelWriter<WatchFileEvent> writer)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var filePath = FilePath.From(path);
            if (Directory.Exists(filePath.Value))
            {
                return;
            }

            writer.TryWrite(new WatchFileEvent(filePath, DateTimeOffset.UtcNow));
        }
        catch
        {
            // ignore malformed paths
        }
    }

    private async Task<TargetFile?> CreateTargetAsync(FilePath path, CancellationToken token)
    {
        var info = await WaitForFileReadyAsync(path, token).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        if (!TryDetermineItemType(path, out var itemType))
        {
            return null;
        }

        return new TargetFile(path, itemType, info.Length);
    }

    private async Task<FileInfo?> WaitForFileReadyAsync(FilePath path, CancellationToken token)
    {
        var timeout = TimeSpan.FromMilliseconds(_options.ReadyTimeoutMilliseconds);
        var deadline = DateTimeOffset.UtcNow + timeout;
        FileInfo? info = null;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                info = new FileInfo(path.Value);
                if (!info.Exists)
                {
                    return null;
                }

                using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = info.Length;
                await Task.Delay(200, token).ConfigureAwait(false);
                info.Refresh();
                if (info.Length == length && info.Length > 0)
                {
                    return info;
                }
            }
            catch (IOException)
            {
                await Task.Delay(200, token).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }

        return info;
    }

    private static bool TryDetermineItemType(FilePath path, out ItemType type)
    {
        if (MediaFormats.IsImage(path))
        {
            type = ItemType.Image;
            return true;
        }

        if (MediaFormats.IsVideo(path))
        {
            type = ItemType.Video;
            return true;
        }

        if (MediaFormats.IsPdf(path))
        {
            type = ItemType.Pdf;
            return true;
        }

        if (ShouldConvertDocuments() && MediaFormats.IsDocument(path))
        {
            type = ItemType.Document;
            return true;
        }

        type = ItemType.Unknown;
        return false;
    }

    private static bool ShouldConvertDocuments() => SettingsHost.Get(SettingsRegistry.AutoConvertDocumentsToPdf);

    private static bool IsWithinWorkRoot(FilePath path)
    {
        var workRoot = SquishPaths.WorkRoot.Value;
        if (!Path.EndsInDirectorySeparator(workRoot))
        {
            workRoot += Path.DirectorySeparatorChar;
        }

        return path.Value.StartsWith(workRoot, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, object?> BuildMetadata(WatchFileEvent watchEvent)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "cli.watch",
            ["watch.observedAt"] = watchEvent.ObservedAt,
            ["watch.recursive"] = _options.Recursive,
            ["watch.debounceMs"] = _options.DebounceMilliseconds
        };

        if (_options.Aggressive)
        {
            metadata["watch.aggressive"] = true;
            metadata[OptimisationMetadata.ImageAggressive] = true;
            metadata[OptimisationMetadata.VideoAggressive] = true;
            metadata[OptimisationMetadata.PdfAggressive] = true;
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

        return metadata;
    }

    private readonly record struct WatchFileEvent(FilePath Path, DateTimeOffset ObservedAt);

    private readonly record struct WatchRequestContext(TargetFile Target, string SourcePath, DateTimeOffset ObservedAt);
}
