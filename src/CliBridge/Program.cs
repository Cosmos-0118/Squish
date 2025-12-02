using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;
using ClopWindows.Core.Shared.Logging;

namespace ClopWindows.CliBridge;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var sharedLogging = SharedLogging.EnableSharedLogger("cli");

        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            Console.Error.WriteLine("Clop CLI requires Windows 7 or later.");
            return 1;
        }

        var root = new RootCommand("Clop for Windows CLI");
        root.Name = "clop";
        root.AddCommand(OptimiseCommandBuilder.Create());
        root.AddCommand(WatchCommandBuilder.Create());
        root.AddCommand(SchemaCommandBuilder.Create(root));
        return await root.InvokeAsync(args).ConfigureAwait(false);
    }
}

internal static class OptimiseCommandBuilder
{
    public static Command Create()
    {
        var command = new Command("optimise", "Optimise images, videos, and PDFs using the Windows pipeline.");
        var items = new Argument<List<string>>("items", "Images, videos, PDFs, or folders to optimise")
        {
            Arity = ArgumentArity.OneOrMore
        };
        command.AddArgument(items);

        var recursive = new Option<bool>(new[] { "-r", "--recursive" }, "Optimise files inside subfolders when passing directories.");
        var skipErrors = new Option<bool>(new[] { "-s", "--skip-errors" }, "Skip files that are missing or unsupported.");
        var aggressive = new Option<bool>(new[] { "-a", "--aggressive" }, "Use aggressive optimisation presets.");
        var adaptive = new Option<bool>("--adaptive-optimisation", description: "Match macOS adaptive conversion heuristics (not yet available on Windows).");
        var removeAudio = new Option<bool>("--remove-audio", () => false, "Strip audio tracks from videos.");
        var json = new Option<bool>(new[] { "-j", "--json" }, "Return optimisation results as JSON.");
        var noProgress = new Option<bool>(new[] { "-n", "--no-progress" }, "Suppress in-flight progress output.");
        var copy = new Option<bool>(new[] { "-c", "--copy" }, "Copy optimised files to the clipboard (not yet available on Windows).");
        var gui = new Option<bool>(new[] { "-g", "--gui" }, "Show the floating HUD UI (not yet available on Windows).");
        var asyncOption = new Option<bool>("--async", "Queue work without waiting for completion (not yet available on Windows).");
        var playbackSpeed = new Option<double?>("--playback-speed-factor", "Speed up or slow down videos (1.0 means no change).");
        var types = new Option<string[]>("--types", description: "File types/extensions to include (e.g. image, video, png, mp4)")
        {
            AllowMultipleArgumentsPerToken = true
        };
        var excludeTypes = new Option<string[]>("--exclude-types", description: "File types/extensions to exclude")
        {
            AllowMultipleArgumentsPerToken = true
        };

        command.AddOption(recursive);
        command.AddOption(skipErrors);
        command.AddOption(aggressive);
        command.AddOption(removeAudio);
        command.AddOption(json);
        command.AddOption(noProgress);
        command.AddOption(copy);
        command.AddOption(gui);
        command.AddOption(asyncOption);
        command.AddOption(adaptive);
        command.AddOption(playbackSpeed);
        command.AddOption(types);
        command.AddOption(excludeTypes);

        command.SetHandler(async context =>
        {
            var parse = context.ParseResult;
            var options = new OptimiseCommandOptions
            {
                Items = parse.GetValueForArgument(items) ?? new List<string>(),
                Recursive = parse.GetValueForOption(recursive),
                SkipErrors = parse.GetValueForOption(skipErrors),
                Aggressive = parse.GetValueForOption(aggressive),
                RemoveAudio = parse.GetValueForOption(removeAudio),
                AdaptiveOptimisation = parse.GetValueForOption(adaptive),
                Json = parse.GetValueForOption(json),
                ShowProgress = !parse.GetValueForOption(noProgress),
                CopyToClipboard = parse.GetValueForOption(copy),
                ShowGui = parse.GetValueForOption(gui),
                Async = parse.GetValueForOption(asyncOption),
                PlaybackSpeedFactor = parse.GetValueForOption(playbackSpeed),
                IncludeTypes = parse.GetValueForOption(types) ?? Array.Empty<string>(),
                ExcludeTypes = parse.GetValueForOption(excludeTypes) ?? Array.Empty<string>()
            };

            var handler = new OptimiseCommandHandler(options);
            try
            {
                context.ExitCode = await handler.ExecuteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("CLI optimise command failed", ex);
                Console.Error.WriteLine($"[error] {ex.Message}");
                context.ExitCode = 1;
            }
        });

        return command;
    }
}

internal sealed record OptimiseCommandOptions
{
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    public bool Recursive { get; init; }
    public bool SkipErrors { get; init; }
    public bool Aggressive { get; init; }
    public bool AdaptiveOptimisation { get; init; }
    public bool RemoveAudio { get; init; }
    public bool Json { get; init; }
    public bool ShowProgress { get; init; } = true;
    public bool CopyToClipboard { get; init; }
    public bool ShowGui { get; init; }
    public bool Async { get; init; }
    public double? PlaybackSpeedFactor { get; init; }
    public IReadOnlyList<string> IncludeTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeTypes { get; init; } = Array.Empty<string>();
}

internal sealed class OptimiseCommandHandler
{
    private readonly OptimiseCommandOptions _options;

    public OptimiseCommandHandler(OptimiseCommandOptions options)
    {
        _options = options;
    }

    public async Task<int> ExecuteAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            throw new PlatformNotSupportedException("Clop CLI requires Windows 7 or later.");
        }

        WarnUnsupportedFlags();

        var resolver = new TargetResolver(_options);
        var targets = resolver.Resolve();
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("No matching files were found.");
            return 1;
        }

        var requestMap = new Dictionary<string, TargetFile>(StringComparer.Ordinal);
        var successes = new List<CliSuccess>();
        var failures = new List<CliFailure>();
        var progressPrinter = new ProgressPrinter(_options.ShowProgress);

        var imageOptions = _options.Aggressive
            ? ImageOptimiserOptions.Default with { TargetJpegQuality = 68, RequireSizeImprovement = true }
            : ImageOptimiserOptions.Default;
        var videoOptions = (VideoOptimiserOptions.Default with
        {
            AggressiveQuality = _options.Aggressive,
            RemoveAudio = _options.RemoveAudio
        }).WithHardwareOverride();
        var pdfOptions = PdfOptimiserOptions.Default with { AggressiveByDefault = _options.Aggressive };

        await using var coordinator = new OptimisationCoordinator(
            new IOptimiser[]
            {
                new ImageOptimiser(imageOptions),
                new VideoOptimiser(videoOptions),
                new PdfOptimiser(pdfOptions)
            },
            Math.Max(1, Environment.ProcessorCount / 2));

        coordinator.ProgressChanged += (_, e) =>
        {
            if (requestMap.TryGetValue(e.Progress.RequestId, out var file))
            {
                progressPrinter.Report(file, e.Progress);
            }
        };

        var tickets = new List<OptimisationTicket>();
        foreach (var target in targets)
        {
            var metadata = BuildMetadata(target.ItemType);
            var request = new OptimisationRequest(target.ItemType, target.Path, metadata: metadata);
            requestMap[request.RequestId] = target;
            tickets.Add(coordinator.Enqueue(request));
        }

        var results = await Task.WhenAll(tickets.Select(t => t.Completion)).ConfigureAwait(false);

        foreach (var result in results)
        {
            var source = requestMap.TryGetValue(result.RequestId, out var target)
                ? target
                : new TargetFile(result.OutputPath ?? FilePath.From(result.RequestId), ItemType.Unknown, 0);

            if (result.Status == OptimisationStatus.Succeeded)
            {
                var entry = CliSuccess.FromResult(result, source);
                successes.Add(entry);
            }
            else
            {
                var entry = CliFailure.FromResult(result, source);
                failures.Add(entry);
            }
        }

        if (!_options.Json)
        {
            foreach (var entry in successes.OrderBy(s => s.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(entry.FormatForConsole());
            }

            foreach (var entry in failures.OrderBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(entry.FormatForConsole());
            }

            PrintSummary(successes);
        }
        else
        {
            var payload = new CliResult(successes, failures);
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            Console.WriteLine(json);
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private IReadOnlyDictionary<string, object?>? BuildMetadata(ItemType type)
    {
        if (type == ItemType.Video)
        {
            var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["video.removeAudio"] = _options.RemoveAudio,
                ["video.aggressive"] = _options.Aggressive
            };
            if (_options.PlaybackSpeedFactor.HasValue)
            {
                metadata["video.playbackSpeed"] = _options.PlaybackSpeedFactor.Value;
            }
            return metadata;
        }

        if (type == ItemType.Pdf)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["pdf.aggressive"] = _options.Aggressive
            };
        }

        return null;
    }

    private static void PrintSummary(IReadOnlyCollection<CliSuccess> successes)
    {
        if (successes.Count <= 1)
        {
            return;
        }

        var totalOriginal = successes.Sum(s => s.OriginalBytes);
        var totalOptimised = successes.Sum(s => s.OptimisedBytes ?? s.OriginalBytes);
        if (totalOriginal <= 0)
        {
            return;
        }

        var delta = totalOriginal - totalOptimised;
        var percentage = totalOriginal == 0 ? 0d : (double)delta / totalOriginal * 100d;
        var direction = delta >= 0 ? "saving" : "adding";
        var pointer = delta >= 0 ? "Saved" : "Added";
        Console.WriteLine($"TOTAL: {totalOriginal.HumanSize()} -> {totalOptimised.HumanSize()} {pointer} {Math.Abs(delta).HumanSize()} ({percentage:+0.##;-0.##;0}% {direction})");
    }

    private void WarnUnsupportedFlags()
    {
        if (_options.CopyToClipboard)
        {
            Console.Error.WriteLine("[info] --copy is not available on Windows yet and will be ignored.");
        }
        if (_options.ShowGui)
        {
            Console.Error.WriteLine("[info] --gui is not available on Windows yet and will be ignored.");
        }
        if (_options.Async)
        {
            Console.Error.WriteLine("[info] --async is not available on Windows yet and will be ignored.");
        }
        if (_options.AdaptiveOptimisation)
        {
            Console.Error.WriteLine("[info] --adaptive-optimisation is not available on Windows yet and will be ignored.");
        }
    }
}

internal sealed record TargetFile(FilePath Path, ItemType ItemType, long OriginalBytes);

internal sealed class TargetResolver
{
    private readonly OptimiseCommandOptions _options;
    private readonly TypeFilter _filter;

    public TargetResolver(OptimiseCommandOptions options)
    {
        _options = options;
        _filter = new TypeFilter(options.IncludeTypes, options.ExcludeTypes);
    }

    public List<TargetFile> Resolve()
    {
        var files = new List<TargetFile>();
        foreach (var item in _options.Items)
        {
            var expanded = ExpandPath(item);
            if (File.Exists(expanded))
            {
                AddFile(expanded, files);
            }
            else if (Directory.Exists(expanded))
            {
                var searchOption = _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(expanded, "*", searchOption))
                {
                    AddFile(file, files);
                }
            }
            else if (!_options.SkipErrors)
            {
                throw new FileNotFoundException($"Item '{item}' was not found.");
            }
            else
            {
                Console.Error.WriteLine($"[warn] Skipping missing item '{item}'.");
            }
        }

        return files;
    }

    private void AddFile(string file, ICollection<TargetFile> files)
    {
        var extension = Path.GetExtension(file).TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension) || !_filter.Allows(extension))
        {
            return;
        }

        ItemType? type = DetermineItemType(extension);
        if (type is null)
        {
            if (!_options.SkipErrors)
            {
                throw new InvalidOperationException($"Unsupported file type '{extension}' for '{file}'.");
            }

            Console.Error.WriteLine($"[warn] Skipping unsupported file '{file}'.");
            return;
        }

        var info = new FileInfo(file);
        files.Add(new TargetFile(FilePath.From(info.FullName), type.Value, info.Exists ? info.Length : 0));
    }

    private static ItemType? DetermineItemType(string extension)
    {
        if (MediaFormats.IsImage(extension))
        {
            return ItemType.Image;
        }

        if (MediaFormats.IsVideo(extension))
        {
            return ItemType.Video;
        }

        if (MediaFormats.IsPdf(extension))
        {
            return ItemType.Pdf;
        }

        return null;
    }

    private static string ExpandPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var value = Environment.ExpandEnvironmentVariables(input.Trim('"'));
        if (value.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = Path.Combine(home, value[2..]);
        }
        return value;
    }
}

internal sealed class TypeFilter
{
    private readonly HashSet<string>? _allowed;
    private readonly HashSet<string> _excluded;

    public TypeFilter(IEnumerable<string> include, IEnumerable<string> exclude)
    {
        _allowed = BuildSet(include);
        _excluded = BuildSet(exclude) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public bool Allows(string extension)
    {
        extension = extension.TrimStart('.');
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        if (_excluded.Contains(extension))
        {
            return false;
        }

        if (_allowed is null)
        {
            return true;
        }

        return _allowed.Contains(extension);
    }

    private static HashSet<string>? BuildSet(IEnumerable<string> tokens)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            foreach (var value in ExpandToken(token))
            {
                values.Add(value);
            }
        }

        return values.Count == 0 ? null : values;
    }

    private static IEnumerable<string> ExpandToken(string token)
    {
        var trimmed = token.Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            yield break;
        }

        if (trimmed.Contains(',', StringComparison.Ordinal))
        {
            foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var value in ExpandToken(part))
                {
                    yield return value;
                }
            }
            yield break;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "image":
            case "images":
                foreach (var ext in MediaFormats.ImageExtensionNames)
                {
                    yield return ext;
                }
                yield break;
            case "video":
            case "videos":
                foreach (var ext in MediaFormats.VideoExtensionNames)
                {
                    yield return ext;
                }
                yield break;
            case "pdf":
            case "pdfs":
                foreach (var ext in MediaFormats.PdfExtensionNames)
                {
                    yield return ext;
                }
                yield break;
        }

        yield return trimmed;
    }
}

internal sealed class ProgressPrinter
{
    private readonly bool _enabled;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _lastPercent = new(StringComparer.Ordinal);

    public ProgressPrinter(bool enabled)
    {
        _enabled = enabled;
    }

    public void Report(TargetFile target, OptimisationProgress progress)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_lastPercent.TryGetValue(progress.RequestId, out var value) && progress.Percentage - value < 5 && string.IsNullOrWhiteSpace(progress.Message))
            {
                return;
            }

            _lastPercent[progress.RequestId] = progress.Percentage;
            var message = string.IsNullOrWhiteSpace(progress.Message) ? string.Empty : $" - {progress.Message}";
            Console.Error.WriteLine($"[{progress.Percentage,5:0.0}%] {target.Path.Name}{message}");
        }
    }
}

internal sealed record CliResult(IReadOnlyCollection<CliSuccess> Done, IReadOnlyCollection<CliFailure> Failed);

internal sealed record CliSuccess(string SourcePath, string? OutputPath, string Message, long OriginalBytes, long? OptimisedBytes)
{
    public static CliSuccess FromResult(OptimisationResult result, TargetFile target)
    {
        long? optimisedBytes = null;
        if (result.OutputPath is { } output && File.Exists(output.Value))
        {
            optimisedBytes = new FileInfo(output.Value).Length;
        }

        return new CliSuccess(target.Path.Value, result.OutputPath?.Value, result.Message ?? "Optimised", target.OriginalBytes, optimisedBytes);
    }

    public string FormatForConsole()
    {
        var newBytes = OptimisedBytes ?? OriginalBytes;
        var delta = OriginalBytes - newBytes;
        var direction = delta >= 0 ? "Saved" : "Added";
        return $"[✓] {SourcePath}: {Message} ({direction} {Math.Abs(delta).HumanSize()})";
    }
}

internal sealed record CliFailure(string SourcePath, string Message)
{
    public static CliFailure FromResult(OptimisationResult result, TargetFile target)
    {
        return new CliFailure(target.Path.Value, result.Message ?? "Optimisation failed");
    }

    public string FormatForConsole() => $"[x] {SourcePath}: {Message}";
}
