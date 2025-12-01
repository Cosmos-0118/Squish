using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public sealed class OptimisationCoordinator : IAsyncDisposable
{
    private readonly Channel<OptimisationWorkItem> _channel;
    private readonly ConcurrentDictionary<ItemType, IOptimiser> _optimisers;
    private readonly List<Task> _workers = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<string, OptimisationStatus> _status = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OptimisationRequest> _requests = new(StringComparer.Ordinal);
    private readonly int _degreeOfParallelism;

    public OptimisationCoordinator(IEnumerable<IOptimiser> optimisers, int degreeOfParallelism = 2)
    {
        if (degreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degreeOfParallelism));
        }

        ArgumentNullException.ThrowIfNull(optimisers);
        var optimiserList = optimisers.ToList();
        _degreeOfParallelism = degreeOfParallelism;
        _optimisers = new ConcurrentDictionary<ItemType, IOptimiser>(optimiserList.Select(o => new KeyValuePair<ItemType, IOptimiser>(o.ItemType, o)), EqualityComparer<ItemType>.Default);
        _channel = Channel.CreateUnbounded<OptimisationWorkItem>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        for (var i = 0; i < _degreeOfParallelism; i++)
        {
            _workers.Add(Task.Run(ProcessQueueAsync));
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

        var workItem = new OptimisationWorkItem(request, cancellationToken);
        _requests[request.RequestId] = request;
        _status[request.RequestId] = OptimisationStatus.Queued;
        ReportProgress(new OptimisationProgress(request.RequestId, 0d, "Queued"));

        if (!_channel.Writer.TryWrite(workItem))
        {
            _status[request.RequestId] = OptimisationStatus.Failed;
            var failed = Task.FromResult(OptimisationResult.Failure(request.RequestId, "Unable to queue work item."));
            return new OptimisationTicket(request.RequestId, failed);
        }

        return new OptimisationTicket(request.RequestId, workItem.Completion.Task);
    }

    public OptimisationStatus GetStatus(string requestId)
    {
        return _status.TryGetValue(requestId, out var status) ? status : OptimisationStatus.Queued;
    }

    public IReadOnlyCollection<OptimisationRequest> PendingRequests => _requests.Values.ToList();

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var workItem))
                {
                    await ProcessWorkItemAsync(workItem).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProcessWorkItemAsync(OptimisationWorkItem workItem)
    {
        var request = workItem.Request;
        if (workItem.CancellationToken.IsCancellationRequested)
        {
            var cancelled = OptimisationResult.Cancelled(request.RequestId);
            _status[request.RequestId] = OptimisationStatus.Cancelled;
            workItem.Completion.TrySetCanceled(workItem.CancellationToken);
            RequestFailed?.Invoke(this, new OptimisationCompletedEventArgs(cancelled));
            return;
        }

        if (!_optimisers.TryGetValue(request.ItemType, out var optimiser))
        {
            var unsupported = OptimisationResult.Unsupported(request.RequestId);
            _status[request.RequestId] = OptimisationStatus.Unsupported;
            workItem.Completion.TrySetResult(unsupported);
            RequestFailed?.Invoke(this, new OptimisationCompletedEventArgs(unsupported));
            return;
        }

        _status[request.RequestId] = OptimisationStatus.Running;
        var started = DateTimeOffset.UtcNow;
        ReportProgress(new OptimisationProgress(request.RequestId, 0d, "Starting"));

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(workItem.CancellationToken, _shutdownCts.Token);
            var context = new OptimiserExecutionContext(request.RequestId, ReportProgress);
            var result = await optimiser.OptimiseAsync(request, context, linked.Token).ConfigureAwait(false);
            var duration = DateTimeOffset.UtcNow - started;
            result = result with { Duration = duration };
            _status[request.RequestId] = result.Status;
            workItem.Completion.TrySetResult(result);

            if (result.Status == OptimisationStatus.Succeeded)
            {
                var target = result.OutputPath ?? request.SourcePath;
                if (target.Exists)
                {
                    ClopOptimisationMarker.TryMark(target);
                }

                RequestCompleted?.Invoke(this, new OptimisationCompletedEventArgs(result));
            }
            else
            {
                RequestFailed?.Invoke(this, new OptimisationCompletedEventArgs(result));
            }
        }
        catch (OperationCanceledException)
        {
            var cancelled = OptimisationResult.Cancelled(request.RequestId);
            _status[request.RequestId] = OptimisationStatus.Cancelled;
            workItem.Completion.TrySetCanceled();
            RequestFailed?.Invoke(this, new OptimisationCompletedEventArgs(cancelled));
        }
        catch (Exception ex)
        {
            var failed = OptimisationResult.Failure(request.RequestId, ex.Message);
            _status[request.RequestId] = OptimisationStatus.Failed;
            workItem.Completion.TrySetResult(failed);
            RequestFailed?.Invoke(this, new OptimisationCompletedEventArgs(failed));
        }
        finally
        {
            _requests.TryRemove(request.RequestId, out _);
        }
    }

    private void ReportProgress(OptimisationProgress progress)
    {
        ProgressChanged?.Invoke(this, new OptimisationProgressEventArgs(progress));
    }

    public async Task StopAsync()
    {
        _shutdownCts.Cancel();
        _channel.Writer.TryComplete();
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
        _shutdownCts.Dispose();
    }

    private sealed class OptimisationWorkItem
    {
        public OptimisationWorkItem(OptimisationRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<OptimisationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public OptimisationRequest Request { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<OptimisationResult> Completion { get; }
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
