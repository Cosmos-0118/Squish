using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Squish.Core.Shared;

namespace Squish.Core.Optimizers;

public sealed class OptimisationCoordinator : IAsyncDisposable
{
    // Two queues, so a picture never waits behind PDFs: light work (images) is served by degreeOfParallelism workers, heavy
    // work by its own few workers.
    private readonly Channel<OptimisationWorkItem> _lightChannel;
    private readonly Channel<OptimisationWorkItem> _heavyChannel;
    private readonly ConcurrentDictionary<ItemType, IOptimiser> _optimisers;
    private readonly List<Task> _workers = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<string, OptimisationStatus> _status = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OptimisationRequest> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OptimisationWorkItem> _workItems = new(StringComparer.Ordinal);
    private readonly int _degreeOfParallelism;

    // PDF, Office and video jobs each stage gigabytes of temp data and hold large documents / frames in memory for a long
    // time; images are comparatively light. Heavy jobs have workers of their own, so at most this many run at once however
    // many are queued: a folder of 12 PDFs cannot unpack 12 documents at the same time while they wait for the pixel budget.
    public const int DefaultMaxConcurrentHeavyJobs = 2;

    // After a burst, wait this long with nothing running or queued before handing freed memory back to Windows.
    private static readonly TimeSpan IdleReclaimDelay = TimeSpan.FromSeconds(3);
    private readonly Timer _idleReclaimTimer;
    private int _activeWorkItems;

    public OptimisationCoordinator(IEnumerable<IOptimiser> optimisers, int degreeOfParallelism = 2, int maxConcurrentHeavyJobs = DefaultMaxConcurrentHeavyJobs)
    {
        if (degreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degreeOfParallelism));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentHeavyJobs);

        ArgumentNullException.ThrowIfNull(optimisers);
        var optimiserList = optimisers.ToList();
        _degreeOfParallelism = degreeOfParallelism;
        _optimisers = new ConcurrentDictionary<ItemType, IOptimiser>(optimiserList.Select(o => new KeyValuePair<ItemType, IOptimiser>(o.ItemType, o)), EqualityComparer<ItemType>.Default);
        // Unbounded: Enqueue is synchronous, so a bounded queue could only reject work once full. Folder drops,
        // watch folders and CLI batches routinely queue hundreds of files; each queued item is just a request record.
        var channelOptions = new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        };
        _lightChannel = Channel.CreateUnbounded<OptimisationWorkItem>(channelOptions);
        _heavyChannel = Channel.CreateUnbounded<OptimisationWorkItem>(channelOptions);

        _idleReclaimTimer = new Timer(_ => ReclaimIfIdle(), null, Timeout.Infinite, Timeout.Infinite);

        for (var i = 0; i < _degreeOfParallelism; i++)
        {
            _workers.Add(Task.Run(() => ProcessQueueAsync(_lightChannel)));
        }

        for (var i = 0; i < maxConcurrentHeavyJobs; i++)
        {
            _workers.Add(Task.Run(() => ProcessQueueAsync(_heavyChannel)));
        }
    }

    public event EventHandler<OptimisationProgressEventArgs>? ProgressChanged;

    public event EventHandler<OptimisationCompletedEventArgs>? RequestCompleted;

    public event EventHandler<OptimisationCompletedEventArgs>? RequestFailed;

    public OptimisationTicket Enqueue(OptimisationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = Task.FromCanceled<OptimisationResult>(cancellationToken);
            return new OptimisationTicket(request.RequestId, cancelled);
        }

        var workItem = new OptimisationWorkItem(request, cancellationToken, _shutdownCts.Token);
        _requests[request.RequestId] = request;
        _status[request.RequestId] = OptimisationStatus.Queued;
        ReportProgress(new OptimisationProgress(request.RequestId, 0d, "Queued"));

        return EnqueueWorkItem(workItem);
    }

    private OptimisationTicket EnqueueWorkItem(OptimisationWorkItem workItem)
    {
        // Track before publishing: a worker may finish (and clear the state) before TryWrite returns.
        _workItems[workItem.Request.RequestId] = workItem;
        if (ChannelFor(workItem.Request.ItemType).Writer.TryWrite(workItem))
        {
            return new OptimisationTicket(workItem.Request.RequestId, workItem.Completion.Task);
        }

        _status[workItem.Request.RequestId] = OptimisationStatus.Failed;
        var failed = Task.FromResult(OptimisationResult.Failure(workItem.Request.RequestId, "Unable to queue work item."));
        ClearRequestState(workItem.Request.RequestId);
        return new OptimisationTicket(workItem.Request.RequestId, failed);
    }

    public OptimisationStatus GetStatus(string requestId)
    {
        return _status.TryGetValue(requestId, out var status) ? status : OptimisationStatus.Unknown;
    }

    public bool Cancel(string requestId, string? reason = null)
    {
        if (_workItems.TryGetValue(requestId, out var workItem))
        {
            _status[requestId] = OptimisationStatus.Cancelled;
            workItem.Cancel();
            ReportProgress(new OptimisationProgress(requestId, 0d, string.IsNullOrWhiteSpace(reason) ? "Cancelling" : reason));
            return true;
        }

        return false;
    }

    /// <summary>Looks up a queued or running request. Gone once it finishes, i.e. before its completion event fires.</summary>
    public bool TryGetRequest(string requestId, out OptimisationRequest request)
    {
        if (_requests.TryGetValue(requestId, out var found))
        {
            request = found;
            return true;
        }

        request = null!;
        return false;
    }

    public IReadOnlyCollection<OptimisationRequest> PendingRequests => _requests.Values.ToList();

    private Channel<OptimisationWorkItem> ChannelFor(ItemType type) => IsHeavy(type) ? _heavyChannel : _lightChannel;

    private int QueuedCount => _lightChannel.Reader.Count + _heavyChannel.Reader.Count;

    private async Task ProcessQueueAsync(Channel<OptimisationWorkItem> channel)
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var workItem))
                {
                    Interlocked.Increment(ref _activeWorkItems);
                    _idleReclaimTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    try
                    {
                        await ProcessWorkItemAsync(workItem).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref _activeWorkItems) == 0 && QueuedCount == 0)
                        {
                            ScheduleIdleReclaim();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void ScheduleIdleReclaim()
    {
        try
        {
            _idleReclaimTimer.Change(IdleReclaimDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private void ReclaimIfIdle()
    {
        if (Volatile.Read(ref _activeWorkItems) == 0 && QueuedCount == 0)
        {
            MemoryReclaimer.Reclaim();
        }
    }

    private async Task ProcessWorkItemAsync(OptimisationWorkItem workItem)
    {
        var request = workItem.Request;
        if (workItem.Token.IsCancellationRequested)
        {
            var cancelled = OptimisationResult.Cancelled(request.RequestId);
            _status[request.RequestId] = OptimisationStatus.Cancelled;
            ClearRequestState(request.RequestId);
            workItem.Completion.TrySetCanceled(workItem.Token);
            Raise(RequestFailed, cancelled);
            return;
        }

        if (!_optimisers.TryGetValue(request.ItemType, out var optimiser))
        {
            var unsupported = OptimisationResult.Unsupported(request.RequestId);
            _status[request.RequestId] = OptimisationStatus.Unsupported;
            ClearRequestState(request.RequestId);
            workItem.Completion.TrySetResult(unsupported);
            Raise(RequestFailed, unsupported);
            return;
        }

        _status[request.RequestId] = OptimisationStatus.Running;
        var started = DateTimeOffset.UtcNow;
        ReportProgress(new OptimisationProgress(request.RequestId, 0d, "Starting"));

        try
        {
            var context = new OptimiserExecutionContext(request.RequestId, ReportProgress);
            var result = await optimiser.OptimiseAsync(request, context, workItem.Token).ConfigureAwait(false);
            var duration = DateTimeOffset.UtcNow - started;
            result = result with { Duration = duration };
            _status[request.RequestId] = result.Status;
            ClearRequestState(request.RequestId);

            // Mark before completing the ticket so anyone awaiting it (watchers, clipboard) already sees the marker.
            if (result.Status == OptimisationStatus.Succeeded)
            {
                var target = result.OutputPath ?? request.SourcePath;
                if (target.Exists)
                {
                    SquishOptimisationMarker.TryMark(target);
                }
            }

            workItem.Completion.TrySetResult(result);

            if (result.Status == OptimisationStatus.Succeeded)
            {
                Raise(RequestCompleted, result);
            }
            else
            {
                Raise(RequestFailed, result);
            }
        }
        catch (OperationCanceledException)
        {
            var cancelled = OptimisationResult.Cancelled(request.RequestId);
            _status[request.RequestId] = OptimisationStatus.Cancelled;
            ClearRequestState(request.RequestId);
            workItem.Completion.TrySetCanceled(workItem.Token);
            Raise(RequestFailed, cancelled);
        }
        catch (Exception ex)
        {
            var failed = OptimisationResult.Failure(request.RequestId, ex.Message);
            _status[request.RequestId] = OptimisationStatus.Failed;
            ClearRequestState(request.RequestId);
            workItem.Completion.TrySetResult(failed);
            Raise(RequestFailed, failed);
        }
        finally
        {
            ClearRequestState(request.RequestId);
        }
    }

    private static bool IsHeavy(ItemType type) => type is ItemType.Pdf or ItemType.Document or ItemType.Video or ItemType.ClipboardVideo;

    private void ReportProgress(OptimisationProgress progress)
    {
        var handler = ProgressChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new OptimisationProgressEventArgs(progress));
        }
        catch (Exception ex)
        {
            Log.Warning($"Optimisation progress subscriber threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Subscribers run on the worker that finished the job. A throwing subscriber must never be able to
    /// flip an already-completed result into a failure or take a worker down with it.
    /// </summary>
    private void Raise(EventHandler<OptimisationCompletedEventArgs>? handler, OptimisationResult result)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new OptimisationCompletedEventArgs(result));
        }
        catch (Exception ex)
        {
            Log.Warning($"Optimisation completion subscriber threw for {result.RequestId}: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        _lightChannel.Writer.TryComplete();
        _heavyChannel.Writer.TryComplete();
        DrainPendingWorkItems("Shutdown requested");
        _shutdownCts.Cancel();
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _idleReclaimTimer.DisposeAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();
    }

    private void DrainPendingWorkItems(string reason)
    {
        foreach (var channel in new[] { _lightChannel, _heavyChannel })
        {
            while (channel.Reader.TryRead(out var workItem))
            {
                CancelPendingWorkItem(workItem, reason);
            }
        }
    }

    private void CancelPendingWorkItem(OptimisationWorkItem workItem, string reason)
    {
        var requestId = workItem.Request.RequestId;
        var cancelled = new OptimisationResult(requestId, OptimisationStatus.Cancelled, null, reason);
        _status[requestId] = OptimisationStatus.Cancelled;
        workItem.Cancel();
        workItem.Completion.TrySetCanceled(workItem.Token);
        Raise(RequestFailed, cancelled);
        ClearRequestState(requestId);
    }

    private void ClearRequestState(string requestId)
    {
        _status.TryRemove(requestId, out _);
        _requests.TryRemove(requestId, out _);
        _workItems.TryRemove(requestId, out _);
    }

    private sealed class OptimisationWorkItem
    {
        public OptimisationWorkItem(OptimisationRequest request, CancellationToken cancellationToken, CancellationToken shutdownToken)
        {
            Request = request;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
            Completion = new TaskCompletionSource<OptimisationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public OptimisationRequest Request { get; }
        public CancellationToken Token => _cts.Token;
        public TaskCompletionSource<OptimisationResult> Completion { get; }

        private readonly CancellationTokenSource _cts;

        public void Cancel() => _cts.Cancel();
    }
}

public sealed class OptimisationProgressEventArgs : EventArgs
{
    public OptimisationProgressEventArgs(OptimisationProgress progress)
    {
        Progress = progress;
    }

    public OptimisationProgress Progress { get; }
}

public sealed class OptimisationCompletedEventArgs : EventArgs
{
    public OptimisationCompletedEventArgs(OptimisationResult result)
    {
        Result = result;
    }

    public OptimisationResult Result { get; }
}
