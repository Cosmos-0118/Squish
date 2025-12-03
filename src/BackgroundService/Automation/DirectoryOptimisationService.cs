using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;
using Microsoft.Extensions.Logging;

namespace ClopWindows.BackgroundService.Automation;

[SupportedOSPlatform("windows")]
public sealed class DirectoryOptimisationService : IAsyncDisposable
{
    private static readonly TimeSpan FileReadyRetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly OptimisationCoordinator _coordinator;
    private readonly ILogger<DirectoryOptimisationService> _logger;
    private readonly Channel<FileEvent> _channel;
    private readonly OptimisedFileRegistry _optimisedFiles;
    private readonly object _watcherGate = new();
    private readonly object _settingsGate = new();
    private readonly Dictionary<string, DirectoryWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DirectoryRequestContext> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ItemType, int> _activeCounts = new();

    private Task? _processingTask;
    private const int MaxRetryAttempts = 24;
    private static readonly TimeSpan QueueRetryDelay = TimeSpan.FromSeconds(2);

    private volatile bool _paused;
    private volatile bool _autoImages;
    private volatile bool _autoVideos;
    private volatile bool _autoPdfs;
    private volatile bool _autoConvertDocuments;

    private string[] _imageDirectories = Array.Empty<string>();
    private string[] _videoDirectories = Array.Empty<string>();
    private string[] _pdfDirectories = Array.Empty<string>();
    private HashSet<string> _skipImageFormats = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _skipVideoFormats = new(StringComparer.OrdinalIgnoreCase);

    private int _maxImageSizeMb;
    private int _maxVideoSizeMb;
    private int _maxPdfSizeMb;
    private int _maxImageFileCount;
    private int _maxVideoFileCount;
    private int _maxPdfFileCount;

    public DirectoryOptimisationService(OptimisationCoordinator coordinator, OptimisedFileRegistry optimisedFiles, ILogger<DirectoryOptimisationService> logger)
    {
        _coordinator = coordinator;
        _optimisedFiles = optimisedFiles;
        _logger = logger;
        _channel = Channel.CreateUnbounded<FileEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        SettingsHost.EnsureInitialized();
        RefreshSettings(updateWatchers: false);

        SettingsHost.SettingChanged += OnSettingChanged;
        _coordinator.RequestCompleted += OnCoordinatorRequestFinished;
        _coordinator.RequestFailed += OnCoordinatorRequestFinished;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_watcherGate)
        {
            if (_processingTask is not null)
            {
                throw new InvalidOperationException("Directory optimisation service is already running.");
            }
        }

        UpdateWatchers();

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedSource.Token;
        var processing = Task.Run(() => ProcessLoopAsync(linkedToken), CancellationToken.None);

        lock (_watcherGate)
        {
            _processingTask = processing;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        linkedSource.Cancel();
        _channel.Writer.TryComplete();

        try
        {
            await processing.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }

        lock (_watcherGate)
        {
            _processingTask = null;
        }

        DisposeWatchers();
    }

    public async ValueTask DisposeAsync()
    {
        SettingsHost.SettingChanged -= OnSettingChanged;
        _coordinator.RequestCompleted -= OnCoordinatorRequestFinished;
        _coordinator.RequestFailed -= OnCoordinatorRequestFinished;

        _channel.Writer.TryComplete();

        Task? processing;
        lock (_watcherGate)
        {
            processing = _processingTask;
            _processingTask = null;
        }

        if (processing is not null)
        {
            try
            {
                await processing.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        DisposeWatchers();
    }

    private async Task ProcessLoopAsync(CancellationToken token)
    {
        await foreach (var fileEvent in _channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await ProcessFileEventAsync(fileEvent, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing watcher event for {Path}", fileEvent.Path.Value);
            }
        }
    }

    private async Task ProcessFileEventAsync(FileEvent fileEvent, CancellationToken token, long? sizeHint = null)
    {
        CleanupRecent();

        if (_paused)
        {
            _logger.LogTrace("Automation paused; ignoring watcher event for {Path}.", fileEvent.Path.Value);
            return;
        }

        if (!IsTypeEnabled(fileEvent.ItemType))
        {
            return;
        }

        var path = fileEvent.Path;
        if (!File.Exists(path.Value))
        {
            return;
        }

        if (IsWithinWorkRoot(path))
        {
            return;
        }

        if (ClopFileGuards.IsClopGenerated(path))
        {
            _logger.LogTrace("Skipping {Path}; detected Clop output file.", path.Value);
            return;
        }

        if (ClopOptimisationMarker.HasValidMarker(path))
        {
            _logger.LogTrace("Skipping {Path}; optimisation marker present.", path.Value);
            return;
        }

        if (!MatchesMediaType(fileEvent.ItemType, path))
        {
            return;
        }

        if (WasRecentlyOptimised(path))
        {
            _logger.LogTrace("Skipping {Path} because it was recently optimised.", path.Value);
            return;
        }

        if (ShouldSkipByFormat(fileEvent.ItemType, path))
        {
            _logger.LogTrace("Skipping {Path} due to excluded format.", path.Value);
            return;
        }

        if (!_activePaths.TryAdd(path.Value, 0))
        {
            _logger.LogTrace("Path {Path} already in progress; suppressing duplicate event.", path.Value);
            return;
        }

        var resolvedType = ResolveItemType(fileEvent.ItemType, path);
        if (resolvedType is null)
        {
            _activePaths.TryRemove(path.Value, out _);
            return;
        }

        var itemType = resolvedType.Value;

        var enqueued = false;
        var reservedSlot = false;
        RetryReason? retryReason = null;

        try
        {
            if (sizeHint.HasValue && !WithinSizeLimit(itemType, sizeHint.Value))
            {
                _logger.LogInformation("Skipping {Path}; size {Size:0.0} MB exceeds limit for {Type}.", path.Value, sizeHint.Value / (1024d * 1024d), itemType);
                goto Complete;
            }

            var info = await WaitForFileReadyAsync(path, token).ConfigureAwait(false);
            if (info is null)
            {
                _logger.LogDebug("File {Path} was not ready for optimisation (attempt {Attempt}).", path.Value, fileEvent.Attempts + 1);
                retryReason = RetryReason.FileBusy;
                goto Complete;
            }

            if (!WithinSizeLimit(itemType, info.Length))
            {
                _logger.LogInformation("Skipping {Path}; size {Size:0.0} MB exceeds limit for {Type}.", path.Value, info.Length / (1024d * 1024d), itemType);
                goto Complete;
            }

            if (MatchesRecentFingerprint(path))
            {
                _logger.LogTrace("Skipping {Path}; matches fingerprint from a recent optimisation.", path.Value);
                goto Complete;
            }

            if (!TryReserveSlot(itemType))
            {
                _logger.LogInformation("Deferring optimisation for {Path}; maximum concurrent {Type} jobs reached.", path.Value, itemType);
                retryReason = RetryReason.QueueBusy;
                goto Complete;
            }

            reservedSlot = true;

            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["source"] = "watcher",
                ["watcher.type"] = itemType.ToString(),
                ["watcher.root"] = fileEvent.RootDirectory.Value
            };

            OutputBehaviourSettings.ApplyTo(metadata);
            VideoEncoderPresetSettings.ApplyTo(metadata);

            var request = new OptimisationRequest(itemType, path, metadata: metadata);
            _pending[request.RequestId] = new DirectoryRequestContext(itemType, path);

            _coordinator.Enqueue(request);
            enqueued = true;
        }
        finally
        {
            if (!enqueued)
            {
                _activePaths.TryRemove(path.Value, out _);
                if (reservedSlot)
                {
                    ReleaseSlot(itemType);
                }
            }
        }

    Complete:
        if (retryReason is { } reason)
        {
            ScheduleRetry(fileEvent, reason);
        }
    }

    private static bool IsWithinWorkRoot(FilePath path)
    {
        var workRoot = ClopPaths.WorkRoot.Value;
        if (path.Value.Equals(workRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Path.EndsInDirectorySeparator(workRoot))
        {
            workRoot += Path.DirectorySeparatorChar;
        }

        return path.Value.StartsWith(workRoot, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesMediaType(ItemType type, FilePath path)
    {
        return type switch
        {
            ItemType.Image => MediaFormats.IsImage(path),
            ItemType.Video => MediaFormats.IsVideo(path),
            ItemType.Pdf => MediaFormats.IsPdf(path) || (_autoConvertDocuments && MediaFormats.IsDocument(path)),
            ItemType.Document => MediaFormats.IsDocument(path),
            _ => false
        };
    }

    private ItemType? ResolveItemType(ItemType watcherType, FilePath path)
    {
        return watcherType switch
        {
            ItemType.Image when MediaFormats.IsImage(path) => ItemType.Image,
            ItemType.Video when MediaFormats.IsVideo(path) => ItemType.Video,
            ItemType.Pdf when MediaFormats.IsPdf(path) => ItemType.Pdf,
            ItemType.Pdf when _autoConvertDocuments && MediaFormats.IsDocument(path) => ItemType.Document,
            _ => null
        };
    }

    private bool WithinSizeLimit(ItemType type, long bytes)
    {
        var sizeMb = bytes / (1024d * 1024d);
        return type switch
        {
            ItemType.Image => _maxImageSizeMb <= 0 || sizeMb <= _maxImageSizeMb,
            ItemType.Video => _maxVideoSizeMb <= 0 || sizeMb <= _maxVideoSizeMb,
            ItemType.Pdf => _maxPdfSizeMb <= 0 || sizeMb <= _maxPdfSizeMb,
            ItemType.Document => _maxPdfSizeMb <= 0 || sizeMb <= _maxPdfSizeMb,
            _ => false
        };
    }

    private bool ShouldSkipByFormat(ItemType type, FilePath path)
    {
        var extension = path.Extension;
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return type switch
        {
            ItemType.Image => _skipImageFormats.Contains(extension.TrimStart('.')),
            ItemType.Video => _skipVideoFormats.Contains(extension.TrimStart('.')),
            _ => false
        };
    }

    private bool IsTypeEnabled(ItemType type)
    {
        return type switch
        {
            ItemType.Image => _autoImages,
            ItemType.Video => _autoVideos,
            ItemType.Pdf => _autoPdfs,
            _ => false
        };
    }

    private bool WasRecentlyOptimised(FilePath path)
    {
        return _optimisedFiles.WasPathRecentlyOptimised(path);
    }

    private bool MatchesRecentFingerprint(FilePath path)
    {
        if (!_optimisedFiles.HasFingerprintCandidates)
        {
            return false;
        }

        return FileFingerprint.TryCreate(path, out var fingerprint) &&
               _optimisedFiles.WasFingerprintRecentlyOptimised(fingerprint);
    }

    private async Task<FileInfo?> WaitForFileReadyAsync(FilePath path, CancellationToken token)
    {
        const int maxAttempts = 60;
        long? previousLength = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(path.Value);
                if (!info.Exists)
                {
                    return null;
                }

                using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(FileReadyRetryDelay, token).ConfigureAwait(false);
                continue;
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(FileReadyRetryDelay, token).ConfigureAwait(false);
                continue;
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            if (info.Length <= 0)
            {
                await Task.Delay(FileReadyRetryDelay, token).ConfigureAwait(false);
                continue;
            }

            if (previousLength.HasValue && info.Length == previousLength.Value)
            {
                return info;
            }

            previousLength = info.Length;
            await Task.Delay(FileReadyRetryDelay, token).ConfigureAwait(false);
        }

        return null;
    }

    private static long? TryGetFileLength(FilePath path)
    {
        try
        {
            var info = new FileInfo(path.Value);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TryReserveSlot(ItemType type)
    {
        var limit = GetMaxCount(type);
        if (limit <= 0)
        {
            return true;
        }

        while (true)
        {
            var current = _activeCounts.GetOrAdd(type, 0);
            if (current >= limit)
            {
                return false;
            }

            if (_activeCounts.TryUpdate(type, current + 1, current))
            {
                return true;
            }

            // retry if concurrent update
        }
    }

    private void ReleaseSlot(ItemType type)
    {
        _activeCounts.AddOrUpdate(type, 0, static (_, current) => Math.Max(0, current - 1));
    }

    private int GetMaxCount(ItemType type) => type switch
    {
        ItemType.Image => _maxImageFileCount,
        ItemType.Video => _maxVideoFileCount,
        ItemType.Pdf => _maxPdfFileCount,
        ItemType.Document => _maxPdfFileCount,
        _ => 0
    };

    private void CleanupRecent()
    {
        _optimisedFiles.Cleanup();
    }

    private void OnCoordinatorRequestFinished(object? sender, OptimisationCompletedEventArgs e)
    {
        if (!_pending.TryRemove(e.Result.RequestId, out var context))
        {
            return;
        }

        ReleaseSlot(context.ItemType);
        _activePaths.TryRemove(context.Path.Value, out _);

        if (context.Path.Exists)
        {
            _optimisedFiles.RegisterPath(context.Path);
        }

        if (e.Result.OutputPath is { } outputPath)
        {
            _optimisedFiles.RegisterPath(outputPath);
        }

        if (e.Result.Status == OptimisationStatus.Succeeded)
        {
            _logger.LogDebug("Completed watcher optimisation for {Path} with status {Status}", context.Path.Value, e.Result.Status);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(e.Result.Message))
            {
                _logger.LogWarning("Watcher optimisation {RequestId} finished with status {Status}: {Message}", e.Result.RequestId, e.Result.Status, e.Result.Message);
            }
            else
            {
                _logger.LogWarning("Watcher optimisation {RequestId} finished with status {Status}", e.Result.RequestId, e.Result.Status);
            }
        }
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!IsWatcherSetting(e.Name))
        {
            return;
        }

        RefreshSettings(updateWatchers: true);
    }

    private bool IsWatcherSetting(string name)
    {
        return string.Equals(name, SettingsRegistry.ImageDirs.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.VideoDirs.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.PdfDirs.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.EnableAutomaticImageOptimisations.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.EnableAutomaticVideoOptimisations.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.EnableAutomaticPdfOptimisations.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.PauseAutomaticOptimisations.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.MaxImageSizeMb.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.MaxVideoSizeMb.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.MaxPdfSizeMb.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.AutoConvertDocumentsToPdf.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.MaxImageFileCount.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.MaxVideoFileCount.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.MaxPdfFileCount.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.ImageFormatsToSkip.Name, StringComparison.Ordinal) ||
               string.Equals(name, SettingsRegistry.VideoFormatsToSkip.Name, StringComparison.Ordinal);
    }

    private void RefreshSettings(bool updateWatchers)
    {
        lock (_settingsGate)
        {
            _paused = SettingsHost.Get(SettingsRegistry.PauseAutomaticOptimisations);
            _autoImages = SettingsHost.Get(SettingsRegistry.EnableAutomaticImageOptimisations);
            _autoVideos = SettingsHost.Get(SettingsRegistry.EnableAutomaticVideoOptimisations);
            _autoPdfs = SettingsHost.Get(SettingsRegistry.EnableAutomaticPdfOptimisations);
            _autoConvertDocuments = SettingsHost.Get(SettingsRegistry.AutoConvertDocumentsToPdf);
            _imageDirectories = SettingsHost.Get(SettingsRegistry.ImageDirs) ?? Array.Empty<string>();
            _videoDirectories = SettingsHost.Get(SettingsRegistry.VideoDirs) ?? Array.Empty<string>();
            _pdfDirectories = SettingsHost.Get(SettingsRegistry.PdfDirs) ?? Array.Empty<string>();
            _skipImageFormats = new HashSet<string>(SettingsHost.Get(SettingsRegistry.ImageFormatsToSkip), StringComparer.OrdinalIgnoreCase);
            _skipVideoFormats = new HashSet<string>(SettingsHost.Get(SettingsRegistry.VideoFormatsToSkip), StringComparer.OrdinalIgnoreCase);
            _maxImageSizeMb = SettingsHost.Get(SettingsRegistry.MaxImageSizeMb);
            _maxVideoSizeMb = SettingsHost.Get(SettingsRegistry.MaxVideoSizeMb);
            _maxPdfSizeMb = SettingsHost.Get(SettingsRegistry.MaxPdfSizeMb);
            _maxImageFileCount = SettingsHost.Get(SettingsRegistry.MaxImageFileCount);
            _maxVideoFileCount = SettingsHost.Get(SettingsRegistry.MaxVideoFileCount);
            _maxPdfFileCount = SettingsHost.Get(SettingsRegistry.MaxPdfFileCount);
        }

        if (updateWatchers)
        {
            UpdateWatchers();
        }
    }

    private void UpdateWatchers()
    {
        var desired = new Dictionary<string, WatchDescriptor>(StringComparer.OrdinalIgnoreCase);

        if (!_paused)
        {
            if (_autoImages)
            {
                foreach (var path in _imageDirectories)
                {
                    if (TryCreateDescriptor(path, ItemType.Image, out var descriptor))
                    {
                        desired[descriptor.Key] = descriptor;
                    }
                }
            }

            if (_autoVideos)
            {
                foreach (var path in _videoDirectories)
                {
                    if (TryCreateDescriptor(path, ItemType.Video, out var descriptor))
                    {
                        desired[descriptor.Key] = descriptor;
                    }
                }
            }

            if (_autoPdfs)
            {
                foreach (var path in _pdfDirectories)
                {
                    if (TryCreateDescriptor(path, ItemType.Pdf, out var descriptor))
                    {
                        desired[descriptor.Key] = descriptor;
                    }
                }
            }
        }

        lock (_watcherGate)
        {
            foreach (var key in _watchers.Keys.Except(desired.Keys, StringComparer.OrdinalIgnoreCase).ToList())
            {
                _watchers[key].Dispose();
                _watchers.Remove(key);
                _logger.LogInformation("Stopped watcher for {Key}.", key);
            }

            foreach (var kvp in desired)
            {
                if (_watchers.ContainsKey(kvp.Key))
                {
                    continue;
                }

                try
                {
                    var watcher = new DirectoryWatcher(kvp.Value.Root, kvp.Value.ItemType, OnWatcherEvent, _logger);
                    _watchers[kvp.Key] = watcher;
                    _logger.LogInformation("Started watcher for {Directory} ({Type}).", kvp.Value.Root.Value, kvp.Value.ItemType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start watcher for {Directory}.", kvp.Value.Root.Value);
                }
            }
        }
    }

    private void DisposeWatchers()
    {
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
        }
    }

    public void RegisterExternalOptimisation(FilePath path)
    {
        _optimisedFiles.RegisterPath(path);
    }

    private void OnWatcherEvent(FileEvent fileEvent)
    {
        if (!_channel.Writer.TryWrite(fileEvent))
        {
            _logger.LogWarning("Dropping watcher event for {Path} because the queue is full.", fileEvent.Path.Value);
        }
    }

    private void ScheduleRetry(FileEvent fileEvent, RetryReason reason)
    {
        if (reason == RetryReason.FileBusy && fileEvent.Attempts >= MaxRetryAttempts)
        {
            _logger.LogWarning("Skipping {Path}; file remained busy after {Attempts} attempts.", fileEvent.Path.Value, fileEvent.Attempts);
            return;
        }

        var next = reason == RetryReason.FileBusy
            ? fileEvent with { Attempts = fileEvent.Attempts + 1 }
            : fileEvent;

        var delay = reason == RetryReason.FileBusy
            ? TimeSpan.FromMilliseconds(Math.Min(5000, 250 * Math.Max(1, next.Attempts)))
            : QueueRetryDelay;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (!_channel.Writer.TryWrite(next))
                {
                    _logger.LogWarning("Failed to requeue watcher event for {Path}; channel unavailable.", next.Path.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Retry scheduling failed for {Path}.", next.Path.Value);
            }
        });
    }

    private bool TryCreateDescriptor(string? candidate, ItemType type, out WatchDescriptor descriptor)
    {
        descriptor = default;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var root = FilePath.From(candidate);
            if (!Directory.Exists(root.Value))
            {
                _logger.LogWarning("Watcher directory {Path} does not exist.", root.Value);
                return false;
            }

            descriptor = new WatchDescriptor(type, root);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid watcher directory {Path}.", candidate);
            return false;
        }
    }

    private readonly record struct WatchDescriptor(ItemType ItemType, FilePath Root)
    {
        public string Key => $"{ItemType}:{Root.Value}";
    }

    private readonly record struct DirectoryRequestContext(ItemType ItemType, FilePath Path);

    private readonly record struct FileEvent(ItemType ItemType, FilePath Path, FilePath RootDirectory, DateTimeOffset ObservedAt, int Attempts = 0);

    private enum RetryReason
    {
        FileBusy,
        QueueBusy
    }

    private sealed class DirectoryWatcher : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Action<FileEvent> _callback;
        private readonly ItemType _itemType;
        private readonly FilePath _root;
        private readonly ILogger _logger;

        public DirectoryWatcher(FilePath root, ItemType itemType, Action<FileEvent> callback, ILogger logger)
        {
            _root = root;
            _itemType = itemType;
            _callback = callback;
            _logger = logger;

            _watcher = new FileSystemWatcher(root.Value)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
        }

        private void OnChanged(object sender, FileSystemEventArgs e) => QueueEvent(e.FullPath);

        private void OnRenamed(object sender, RenamedEventArgs e) => QueueEvent(e.FullPath);

        private void QueueEvent(string? path)
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

                _callback(new FileEvent(_itemType, filePath, _root, DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to queue watcher event for {Path}.", path);
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            _logger.LogWarning(e.GetException(), "FileSystemWatcher reported an error for {Directory}.", _root.Value);
        }

        public void Dispose()
        {
            _watcher.Created -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
        }
    }
}
