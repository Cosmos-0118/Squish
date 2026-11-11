using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Squish.Core.Optimizers;
using Squish.Core.Stats;

namespace Squish.App.Services;

/// <summary>
/// Writes every successful optimisation to the statistics history that feeds the Summary page. Covers all sources
/// (drops, clipboard, watched folders, CLI), since they all go through the one coordinator.
/// </summary>
public sealed class OptimisationStatsRecorder : IDisposable
{
    private readonly OptimisationCoordinator _coordinator;
    private readonly OptimisationStatsStore _store;
    private readonly object _gate = new();
    private readonly List<OptimisationRecord> _records;

    // Optimisation rewrites files in place and the coordinator forgets a request before it announces completion,
    // so the type and the "before" size are captured the first time a request shows up (when it is queued).
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private bool _started;

    public OptimisationStatsRecorder(OptimisationCoordinator coordinator, OptimisationStatsStore store)
    {
        _coordinator = coordinator;
        _store = store;
        _records = new List<OptimisationRecord>(store.Load());
    }

    /// <summary>Raised on the thread that finished the optimisation.</summary>
    public event EventHandler? RecordAdded;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _coordinator.ProgressChanged += OnProgressChanged;
        _coordinator.RequestCompleted += OnRequestCompleted;
        _coordinator.RequestFailed += OnRequestFailed;

        foreach (var request in _coordinator.PendingRequests)
        {
            Capture(request);
        }
    }

    public IReadOnlyList<OptimisationRecord> Snapshot()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
        }

        _store.Clear();
        RecordAdded?.Invoke(this, EventArgs.Empty);
    }

    private void OnProgressChanged(object? sender, OptimisationProgressEventArgs e)
    {
        var id = e.Progress.RequestId;
        if (_pending.ContainsKey(id))
        {
            return;
        }

        if (_coordinator.TryGetRequest(id, out var request))
        {
            Capture(request);
        }
    }

    private void Capture(OptimisationRequest request)
    {
        var kind = OptimisationRecord.KindFor(request.ItemType);
        if (kind is null)
        {
            return;
        }

        _pending.TryAdd(request.RequestId, new Pending(kind.Value, TryGetLength(request.SourcePath.Value)));
    }

    private void OnRequestCompleted(object? sender, OptimisationCompletedEventArgs e)
    {
        if (!_pending.TryRemove(e.Result.RequestId, out var pending) || e.Result.Status != OptimisationStatus.Succeeded)
        {
            return;
        }

        var outputPath = e.Result.OutputPath?.Value;
        var optimised = outputPath is null ? null : TryGetLength(outputPath);
        var original = pending.OriginalBytes ?? 0;
        var record = new OptimisationRecord(
            DateTimeOffset.UtcNow,
            pending.Kind,
            original,
            optimised ?? original,
            e.Result.Duration?.TotalSeconds ?? 0d);

        lock (_gate)
        {
            _records.Add(record);
        }

        _store.Append(record);
        RecordAdded?.Invoke(this, EventArgs.Empty);
    }

    private void OnRequestFailed(object? sender, OptimisationCompletedEventArgs e)
    {
        _pending.TryRemove(e.Result.RequestId, out _);
    }

    private static long? TryGetLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _coordinator.ProgressChanged -= OnProgressChanged;
        _coordinator.RequestCompleted -= OnRequestCompleted;
        _coordinator.RequestFailed -= OnRequestFailed;
    }

    private readonly record struct Pending(StatsKind Kind, long? OriginalBytes);
}
