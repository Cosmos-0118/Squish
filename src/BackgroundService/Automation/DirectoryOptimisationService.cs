using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Optimizers;
using Squish.Core.Settings;
using Squish.Core.Shared;
using Microsoft.Extensions.Logging;

namespace Squish.BackgroundService.Automation;

/// <summary>
/// Watches the configured folders and feeds new images and videos to the optimisation coordinator.
/// </summary>
/// <remarks>
/// Pipeline: OS watch (callback only filters by extension) -> <see cref="FileSettleQueue{T}"/> (coalesce, wait for
/// quiet) -> one non-blocking consumer (cheap guards, readiness probe, concurrency gate) -> coordinator.
/// Nothing in the consumer sleeps, so one half-written video cannot hold up the rest. A separate health loop
/// re-creates dead watches and reconciles with a directory scan whenever events may have been lost
/// (buffer overflow, folder removed and restored, network shares).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DirectoryOptimisationService : IAsyncDisposable
{
    private readonly OptimisationCoordinator _coordinator;
    private readonly OptimisedFileRegistry _optimisedFiles;
    private readonly ILogger<DirectoryOptimisationService> _logger;
    private readonly IFolderWatchSettings _settings;
    private readonly bool _ownsSettings;
    private readonly FolderWatchOptions _options;
    private readonly TimeProvider _time;
    private readonly FileSettleQueue<WatchHit> _queue;
    private readonly object _watcherGate = new();
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DirectoryRequestContext> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _healthWake = new(0, int.MaxValue);

    private int _running;
    private volatile bool _disposed;
    private int _activeImages;
    private int _activeVideos;

    private long _hits;
    private long _submitted;
    private long _retries;
    private long _scans;
    private long _scanFound;
    private long _watchRestarts;
    private long _overflows;

    public DirectoryOptimisationService(OptimisationCoordinator coordinator, OptimisedFileRegistry optimisedFiles, ILogger<DirectoryOptimisationService> logger)
        : this(coordinator, optimisedFiles, logger, new SettingsFolderWatchSettings(), FolderWatchOptions.Default, TimeProvider.System, ownsSettings: true)
    {
    }

    internal DirectoryOptimisationService(
        OptimisationCoordinator coordinator,
        OptimisedFileRegistry optimisedFiles,
        ILogger<DirectoryOptimisationService> logger,
        IFolderWatchSettings settings,
        FolderWatchOptions options,
        TimeProvider time,
        bool ownsSettings = false)
    {
        _coordinator = coordinator;
        _optimisedFiles = optimisedFiles;
        _logger = logger;
        _settings = settings;
        _ownsSettings = ownsSettings;
        _options = options;
        _time = time;
        _queue = new FileSettleQueue<WatchHit>(time, static (_, latest) => latest);
    }

    internal DirectoryServiceStats Stats => new(
        Interlocked.Read(ref _hits),
        Interlocked.Read(ref _submitted),
        Interlocked.Read(ref _retries),
        Interlocked.Read(ref _scans),
        Interlocked.Read(ref _scanFound),
        Interlocked.Read(ref _watchRestarts),
        Interlocked.Read(ref _overflows),
        _queue.Count,
        _pending.Count);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("Directory optimisation service is already running.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        try
        {
            _settings.Changed += OnSettingsChanged;
            ApplyPlan();

            var consumer = Task.Run(() => ConsumeAsync(token), CancellationToken.None);
            var health = Task.Run(() => HealthLoopAsync(token), CancellationToken.None);

            // Both loops handle their own per-iteration errors and only finish on cancellation. If one dies anyway,
            // take the other down too so the supervisor restarts the whole service from a clean slate.
            await Task.WhenAny(consumer, health).ConfigureAwait(false);
            linked.Cancel();

            try
            {
                await Task.WhenAll(consumer, health).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // shutdown
            }
        }
        finally
        {
            _settings.Changed -= OnSettingsChanged;
            DisposeWatchers();
            _queue.Clear();
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        DisposeWatchers();
        _queue.Clear();
        if (_ownsSettings && _settings is IDisposable disposable)
        {
            disposable.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public void RegisterExternalOptimisation(FilePath path)
    {
        _optimisedFiles.RegisterPath(path);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            ApplyPlan();
            // Limits may have been raised: let deferred files re-check immediately.
            _queue.WakeDeferred();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed applying folder watcher settings.");
        }
    }

    // ---------------------------------------------------------------- consumer

    private async Task ConsumeAsync(CancellationToken token)
    {
        await foreach (var entry in _queue.ReadDueAsync(token).ConfigureAwait(false))
        {
            try
            {
                ProcessEntry(entry);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed processing watcher event for {Path}", entry.Key);
            }
        }
    }

    private void ProcessEntry(FileSettleQueue<WatchHit>.Entry entry)
    {
        var type = entry.Payload.Type;
        var config = _settings.Current;

        if (config.Paused)
        {
            _logger.LogTrace("Automation paused; ignoring watcher event for {Path}.", entry.Key);
            return;
        }

        if (!config.IsEnabled(type))
        {
            return;
        }

        FilePath path;
        try
        {
            path = FilePath.From(entry.Key);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        // Cheapest guards first: all string work, no I/O.
        if (IsWithinWorkRoot(path) ||
            SquishFileGuards.IsSquishGenerated(path) ||
            config.IsFormatSkipped(type, path.Extension) ||
            _optimisedFiles.WasPathRecentlyOptimised(path) ||
            _activePaths.ContainsKey(path.Value))
        {
            return;
        }

        _optimisedFiles.Cleanup();

        var quiet = QuietPeriod(type);
        // The debounce already waited `quiet` after the last event, and the share-mode probe rules out an active
        // writer, so a file untouched for most of that window counts as finished (avoids a pointless second look
        // when the timer fires a hair early relative to the file's own timestamp).
        var settle = TimeSpan.FromTicks(quiet.Ticks * 4 / 5);
        var probe = FileReadiness.Probe(path.Value, entry.ObservedLength, entry.ObservedTicks, _time.GetUtcNow().UtcDateTime, settle);
        switch (probe.State)
        {
            case FileReadyState.Gone:
                return;
            case FileReadyState.CloudPlaceholder:
                _logger.LogTrace("Skipping {Path}; cloud placeholder (not downloaded).", path.Value);
                return;
            case FileReadyState.Ready:
                break;
            default:
                Retry(entry, probe, quiet);
                return;
        }

        if (!config.WithinSizeLimit(type, probe.Length))
        {
            _logger.LogInformation("Skipping {Path}; size {Size:0.0} MB exceeds limit for {Type}.", path.Value, probe.Length / (1024d * 1024d), type);
            return;
        }

        if (SquishOptimisationMarker.HasValidMarker(path))
        {
            _logger.LogTrace("Skipping {Path}; optimisation marker present.", path.Value);
            return;
        }

        if (MatchesRecentFingerprint(path, probe.Length))
        {
            _logger.LogTrace("Skipping {Path}; matches fingerprint from a recent optimisation.", path.Value);
            return;
        }

        if (!TryReserveSlot(type, config.JobLimit(type)))
        {
            _logger.LogDebug("Deferring {Path}; maximum concurrent {Type} jobs reached.", path.Value, type);
            _queue.Reschedule(entry, _options.DeferredFallback, deferred: true);
            return;
        }

        if (!_activePaths.TryAdd(path.Value, 0))
        {
            ReleaseSlot(type);
            return;
        }

        Submit(type, path, entry.Payload.Root);
    }

    private void Retry(FileSettleQueue<WatchHit>.Entry entry, FileProbe probe, TimeSpan quiet)
    {
        entry.Attempts++;
        Interlocked.Increment(ref _retries);

        if (TimeSpan.FromMilliseconds(_queue.ElapsedMs(entry)) > _options.MaxPendingAge)
        {
            _logger.LogWarning("Skipping {Path}; it never became ready (last state {State}).", entry.Key, probe.State);
            return;
        }

        TimeSpan delay;
        switch (probe.State)
        {
            case FileReadyState.Unstable:
                // Remember what we saw; a second identical look proves the writer is done. Wake right when the
                // file would have been quiet for long enough rather than polling blindly.
                entry.ObservedLength = probe.Length;
                entry.ObservedTicks = probe.LastWriteTicks;
                var age = _time.GetUtcNow().UtcDateTime - new DateTime(probe.LastWriteTicks, DateTimeKind.Utc);
                delay = age < TimeSpan.Zero || age >= quiet
                    ? quiet
                    : Max(quiet - age + TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50));
                break;
            case FileReadyState.Empty:
                delay = quiet;
                break;
            default:
                // Busy: a writer still holds the file. Back off exponentially up to the cap.
                var factor = 1 << Math.Min(entry.Attempts - 1, 6);
                delay = Min(_options.BusyBackoffMax, TimeSpan.FromTicks(_options.BusyBackoffBase.Ticks * factor));
                break;
        }

        if (entry.Attempts == 1)
        {
            _logger.LogDebug("File {Path} not ready yet ({State}); will re-check.", entry.Key, probe.State);
        }

        _queue.Reschedule(entry, delay);
    }

    private void Submit(ItemType type, FilePath path, string root)
    {
        string? requestId = null;

        try
        {
            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["source"] = "watcher",
                ["watcher.type"] = type.ToString(),
                ["watcher.root"] = root
            };
            _settings.ApplyRequestMetadata(type, path, metadata);

            var request = new OptimisationRequest(type, path, metadata: metadata);
            requestId = request.RequestId;
            _pending[requestId] = new DirectoryRequestContext(type, path);

            var ticket = _coordinator.Enqueue(request);
            Interlocked.Increment(ref _submitted);

            // The ticket is the single source of truth for completion: it also covers cancellation and the
            // "could not queue" path, which never raise coordinator events. CompleteRequest is idempotent.
            _ = ticket.Completion.ContinueWith(
                static (task, state) =>
                {
                    var (service, id) = ((DirectoryOptimisationService, string))state!;
                    service.CompleteRequest(id, task.Status == TaskStatus.RanToCompletion ? task.Result : null);
                },
                (this, requestId),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue watcher optimisation for {Path}.", path.Value);
            if (requestId is not null)
            {
                CompleteRequest(requestId, null);
            }
            else
            {
                // Nothing was registered, so release what ProcessEntry reserved.
                _activePaths.TryRemove(path.Value, out _);
                ReleaseSlot(type);
            }
        }
    }

    private void CompleteRequest(string requestId, OptimisationResult? result)
    {
        if (!_pending.TryRemove(requestId, out var context))
        {
            return;
        }

        // Register before releasing the path so a late change event for our own output can never slip between the two.
        if (context.Path.Exists)
        {
            _optimisedFiles.RegisterPath(context.Path);
        }

        if (result?.OutputPath is { } outputPath)
        {
            _optimisedFiles.RegisterPath(outputPath);
        }

        _activePaths.TryRemove(context.Path.Value, out _);
        ReleaseSlot(context.ItemType);
        _queue.WakeDeferred();

        if (result is null)
        {
            _logger.LogWarning("Watcher optimisation {RequestId} for {Path} did not complete.", requestId, context.Path.Value);
        }
        else if (result.Status == OptimisationStatus.Succeeded)
        {
            _logger.LogDebug("Completed watcher optimisation for {Path} with status {Status}", context.Path.Value, result.Status);
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            _logger.LogWarning("Watcher optimisation {RequestId} finished with status {Status}: {Message}", requestId, result.Status, result.Message);
        }
        else
        {
            _logger.LogWarning("Watcher optimisation {RequestId} finished with status {Status}", requestId, result.Status);
        }
    }

    private static bool IsWithinWorkRoot(FilePath path)
    {
        var workRoot = SquishPaths.WorkRoot.Value;
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

    private bool MatchesRecentFingerprint(FilePath path, long length)
    {
        // Hashing reads the whole file, so only do it when a recent fingerprint of this exact size exists.
        if (!_optimisedFiles.HasFingerprintCandidates || !_optimisedFiles.MayMatchFingerprintLength(length))
        {
            return false;
        }

        return FileFingerprint.TryCreate(path, out var fingerprint) &&
               _optimisedFiles.WasFingerprintRecentlyOptimised(fingerprint);
    }

    private TimeSpan QuietPeriod(ItemType type) => type == ItemType.Video ? _options.VideoQuietPeriod : _options.ImageQuietPeriod;

    private bool TryReserveSlot(ItemType type, int limit)
    {
        ref var counter = ref Counter(type);
        if (limit <= 0)
        {
            Interlocked.Increment(ref counter);
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current >= limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseSlot(ItemType type)
    {
        ref var counter = ref Counter(type);
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0 || Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return;
            }
        }
    }

    private ref int Counter(ItemType type)
    {
        if (type == ItemType.Video)
        {
            return ref _activeVideos;
        }

        return ref _activeImages;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    // ---------------------------------------------------------------- watches

    private void ApplyPlan()
    {
        if (_disposed)
        {
            return;
        }

        var plan = _settings.Current.BuildPlan();

        lock (_watcherGate)
        {
            var desired = plan.ToDictionary(spec => spec.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var key in _roots.Keys.Where(key => !desired.ContainsKey(key)).ToList())
            {
                _roots[key].Watcher?.Dispose();
                _roots.Remove(key);
                _logger.LogInformation("Stopped watcher for {Directory}.", key);
            }

            foreach (var spec in plan)
            {
                if (_roots.TryGetValue(spec.Path, out var existing))
                {
                    existing.Spec = spec;
                    if (existing.Watcher is { } watcher)
                    {
                        watcher.AllowedTypes = spec.Types;
                    }

                    continue;
                }

                var state = new RootState(spec) { LastHealthyUtc = _time.GetUtcNow().UtcDateTime };
                _roots[spec.Path] = state;
                TryStartWatcher(state);
            }
        }
    }

    /// <summary>Must be called with <c>_watcherGate</c> held.</summary>
    private bool TryStartWatcher(RootState state)
    {
        var spec = state.Spec;
        var now = _time.GetUtcNow().UtcDateTime;

        if (!Directory.Exists(spec.Path))
        {
            if (!state.ReportedOffline)
            {
                _logger.LogWarning("Watcher directory {Path} is not available; will retry until it appears.", spec.Path);
                state.ReportedOffline = true;
            }

            state.Failures++;
            state.NextAttemptUtc = now + Backoff(state.Failures);
            return false;
        }

        try
        {
            var root = spec.Path;
            state.Watcher = new FolderWatcher(root, spec.Types, (path, types, renamed) => OnWatcherHit(root, path, types, renamed), OnWatcherFault);
            state.Failures = 0;
            state.ReportedOffline = false;
            state.IsPolled = IsPolledLocation(root);
            _logger.LogInformation("Started watcher for {Directory} ({Types}){Mode}.", spec.Path, spec.Types, state.IsPolled ? " with periodic reconciliation" : string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            state.Failures++;
            state.NextAttemptUtc = now + Backoff(state.Failures);
            _logger.LogError(ex, "Failed to start watcher for {Directory}; retrying in {Delay}.", spec.Path, Backoff(state.Failures));
            return false;
        }
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Clamp(failures - 1, 0, 6)));

    private static bool IsPolledLocation(string root)
    {
        // Change notifications from network shares and removable media are best-effort at the OS level.
        try
        {
            var driveRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(driveRoot) || driveRoot.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            return new DriveInfo(driveRoot).DriveType != DriveType.Fixed;
        }
        catch
        {
            return true;
        }
    }

    private void OnWatcherHit(string root, string path, WatchTypes types, bool renamed)
    {
        Interlocked.Increment(ref _hits);
        var type = types == WatchTypes.Video ? ItemType.Video : ItemType.Image;
        var quiet = renamed ? _options.RenameQuietPeriod : QuietPeriod(type);
        _queue.Touch(path, new WatchHit(type, root), quiet);
    }

    private void OnWatcherFault(FolderWatcher watcher, Exception exception)
    {
        if (exception is InternalBufferOverflowException)
        {
            Interlocked.Increment(ref _overflows);
            _logger.LogWarning("File system events overflowed for {Directory}; reconciling with a scan.", watcher.Root);
        }
        else
        {
            _logger.LogWarning(exception, "Watcher for {Directory} failed; it will be recreated.", watcher.Root);
        }

        if (_healthWake.CurrentCount == 0)
        {
            try
            {
                _healthWake.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signalled
            }
        }
    }

    private void DisposeWatchers()
    {
        lock (_watcherGate)
        {
            foreach (var state in _roots.Values)
            {
                state.Watcher?.Dispose();
                state.Watcher = null;
            }

            _roots.Clear();
        }
    }

    // ---------------------------------------------------------------- health + reconciliation

    private async Task HealthLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = _options.HealthInterval;

            try
            {
                delay = await RunHealthPassAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Folder watcher health pass failed.");
            }

            try
            {
                await _healthWake.WaitAsync(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <returns>How long to wait before the next pass.</returns>
    internal async Task<TimeSpan> RunHealthPassAsync(CancellationToken token)
    {
        RootState[] states;
        lock (_watcherGate)
        {
            states = _roots.Values.ToArray();
        }

        var next = _options.HealthInterval;

        foreach (var state in states)
        {
            token.ThrowIfCancellationRequested();

            var rootExists = Directory.Exists(state.Spec.Path);
            var now = _time.GetUtcNow().UtcDateTime;
            var scan = false;
            var since = state.LastHealthyUtc;

            lock (_watcherGate)
            {
                if (!_roots.TryGetValue(state.Spec.Path, out var live) || !ReferenceEquals(live, state))
                {
                    continue;
                }

                if (state.Watcher is { } watcher)
                {
                    if (watcher.IsFaulted || !rootExists)
                    {
                        _logger.LogWarning("Watch on {Directory} was lost ({Reason}); recreating.", state.Spec.Path, rootExists ? "faulted" : "folder missing");
                        watcher.Dispose();
                        state.Watcher = null;
                        state.NextAttemptUtc = now;
                        Interlocked.Increment(ref _watchRestarts);
                    }
                    else
                    {
                        if (watcher.ConsumeOverflow())
                        {
                            scan = true;
                        }

                        if (state.IsPolled && now - state.LastScanUtc >= _options.PollInterval)
                        {
                            scan = true;
                        }
                    }
                }

                if (state.Watcher is null && now >= state.NextAttemptUtc && TryStartWatcher(state))
                {
                    // Anything that happened while we were deaf is only recoverable by looking.
                    scan = true;
                }

                if (state.Watcher is null)
                {
                    next = Min(next, Max(state.NextAttemptUtc - now, TimeSpan.FromMilliseconds(250)));
                }
            }

            if (scan)
            {
                await ScanAsync(state, since, token).ConfigureAwait(false);
            }

            if (state.Watcher is not null)
            {
                state.LastHealthyUtc = _time.GetUtcNow().UtcDateTime;
            }

            if (state.IsPolled)
            {
                next = Min(next, _options.PollInterval);
            }
        }

        return next;
    }

    /// <summary>Queues every media file under the root modified since <paramref name="since"/>.</summary>
    internal Task<int> ScanAsync(RootState state, DateTime since, CancellationToken token)
    {
        return Task.Run(() =>
        {
            var found = 0;
            var cutoff = since - _options.ScanSlack;
            var root = state.Spec.Path;
            var types = state.Spec.Types;

            try
            {
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 32,
                    ReturnSpecialDirectories = false
                };

                var files = new FileSystemEnumerable<(string Path, WatchTypes Type)>(
                    root,
                    static (ref FileSystemEntry entry) => (entry.ToFullPath(), MediaExtensionFilter.Classify(entry.FileName)),
                    enumeration)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                        !entry.IsDirectory &&
                        entry.LastWriteTimeUtc.UtcDateTime >= cutoff &&
                        (MediaExtensionFilter.Classify(entry.FileName) & types) != 0
                };

                foreach (var (path, type) in files)
                {
                    token.ThrowIfCancellationRequested();

                    var itemType = type == WatchTypes.Video ? ItemType.Video : ItemType.Image;
                    _queue.Touch(path, new WatchHit(itemType, root), QuietPeriod(itemType));
                    if (++found >= _options.MaxScanResults)
                    {
                        _logger.LogWarning("Scan of {Directory} hit the {Limit} file limit; remaining files will be picked up by later events.", root, _options.MaxScanResults);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Scan of {Directory} was interrupted.", root);
            }

            state.LastScanUtc = _time.GetUtcNow().UtcDateTime;
            Interlocked.Increment(ref _scans);
            Interlocked.Add(ref _scanFound, found);
            if (found > 0)
            {
                _logger.LogInformation("Reconciliation scan of {Directory} queued {Count} file(s) modified since {Since:u}.", root, found, since);
            }

            return found;
        }, CancellationToken.None);
    }

    // ---------------------------------------------------------------- types

    internal sealed class RootState
    {
        public RootState(WatchRootSpec spec) => Spec = spec;

        public WatchRootSpec Spec { get; set; }

        public FolderWatcher? Watcher { get; set; }

        public bool IsPolled { get; set; }

        public bool ReportedOffline { get; set; }

        public int Failures { get; set; }

        public DateTime NextAttemptUtc { get; set; }

        public DateTime LastHealthyUtc { get; set; }

        public DateTime LastScanUtc { get; set; }
    }

    private readonly record struct WatchHit(ItemType Type, string Root);

    private readonly record struct DirectoryRequestContext(ItemType ItemType, FilePath Path);
}

internal readonly record struct DirectoryServiceStats(
    long WatcherHits,
    long Submitted,
    long Retries,
    long Scans,
    long ScanFound,
    long WatchRestarts,
    long Overflows,
    int Tracked,
    int InFlight);

internal sealed record FolderWatchOptions
{
    public TimeSpan ImageQuietPeriod { get; init; } = TimeSpan.FromMilliseconds(120);

    public TimeSpan VideoQuietPeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A rename-into-place means the writer already finished, so the debounce can be much shorter.</summary>
    public TimeSpan RenameQuietPeriod { get; init; } = TimeSpan.FromMilliseconds(80);

    public TimeSpan BusyBackoffBase { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan BusyBackoffMax { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A file that stays busy or unstable for this long is dropped (a later write event revives it).</summary>
    public TimeSpan MaxPendingAge { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Safety net for files waiting on a free job slot; normally they are woken as soon as a slot frees.</summary>
    public TimeSpan DeferredFallback { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan ScanSlack { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxScanResults { get; init; } = 20_000;

    public static FolderWatchOptions Default { get; } = new();
}
