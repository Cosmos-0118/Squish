using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Squish.BackgroundService.Automation;

/// <summary>
/// Coalesces bursts of file-system events into one entry per path and releases each entry only once the path has
/// been quiet for a while. Waiting happens in a priority queue rather than in the consumer, so a file that is still
/// being written (or a thousand of them) never blocks files that are ready.
/// </summary>
/// <remarks>
/// Producers call <see cref="Touch"/> from any thread (cheap, no I/O). A single consumer iterates
/// <see cref="ReadDueAsync"/> and either finishes an entry or hands it back with <see cref="Reschedule"/>.
/// </remarks>
internal sealed class FileSettleQueue<T>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly PriorityQueue<(Entry Entry, int Version), (long Due, long Sequence)> _schedule = new();
    private readonly TimeProvider _time;
    private readonly Func<T, T, T>? _merge;

    private TaskCompletionSource<bool> _signal = NewSignal();
    private long _consumerWakeAt = long.MaxValue;
    private long _sequence;

    public FileSettleQueue(TimeProvider? timeProvider = null, Func<T, T, T>? merge = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _merge = merge;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Registers activity for <paramref name="key"/>; it becomes due <paramref name="quiet"/> after the last touch.</summary>
    public void Touch(string key, T payload, TimeSpan quiet)
    {
        lock (_gate)
        {
            var due = NowMs() + (long)quiet.TotalMilliseconds;
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Payload = _merge is null ? payload : _merge(entry.Payload, payload);
                // New activity invalidates whatever stability observation we made earlier.
                entry.ObservedLength = -1;
                entry.ObservedTicks = 0;
                Schedule(entry, due);
            }
            else
            {
                entry = new Entry(key, payload, NowMs());
                _entries[key] = entry;
                Schedule(entry, due);
            }

            SignalIfEarlier(due);
        }
    }

    /// <summary>
    /// Puts a previously dequeued entry back, <paramref name="delay"/> from now. If fresh activity re-created the
    /// path in the meantime, the fresh entry wins and inherits the retry bookkeeping.
    /// </summary>
    public void Reschedule(Entry entry, TimeSpan delay, bool deferred = false)
    {
        lock (_gate)
        {
            var due = NowMs() + (long)delay.TotalMilliseconds;
            if (_entries.TryGetValue(entry.Key, out var current))
            {
                if (!ReferenceEquals(current, entry))
                {
                    current.Attempts = Math.Max(current.Attempts, entry.Attempts);
                    current.FirstSeenMs = Math.Min(current.FirstSeenMs, entry.FirstSeenMs);
                    return;
                }
            }
            else
            {
                _entries[entry.Key] = entry;
            }

            entry.Deferred = deferred;
            Schedule(entry, due);
            SignalIfEarlier(due);
        }
    }

    /// <summary>Makes every deferred entry due immediately (a job slot was freed).</summary>
    public void WakeDeferred()
    {
        lock (_gate)
        {
            var now = NowMs();
            var any = false;
            foreach (var entry in _entries.Values)
            {
                if (entry.Deferred && entry.DueMs > now)
                {
                    Schedule(entry, now);
                    any = true;
                }
            }

            if (any)
            {
                SignalIfEarlier(now);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _schedule.Clear();
        }
    }

    public long ElapsedMs(Entry entry)
    {
        lock (_gate)
        {
            return NowMs() - entry.FirstSeenMs;
        }
    }

    public async IAsyncEnumerable<Entry> ReadDueAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Entry? ready = null;
            Task? signal = null;
            long waitMs = Timeout.Infinite;

            lock (_gate)
            {
                var now = NowMs();
                while (_schedule.TryPeek(out var head, out var priority))
                {
                    if (head.Version != head.Entry.Version || !_entries.TryGetValue(head.Entry.Key, out var live) || !ReferenceEquals(live, head.Entry))
                    {
                        _schedule.Dequeue();
                        continue;
                    }

                    if (priority.Due <= now)
                    {
                        _schedule.Dequeue();
                        _entries.Remove(head.Entry.Key);
                        ready = head.Entry;
                    }
                    else
                    {
                        waitMs = priority.Due - now;
                        _consumerWakeAt = priority.Due;
                    }

                    break;
                }

                if (ready is null)
                {
                    if (waitMs == Timeout.Infinite)
                    {
                        _consumerWakeAt = long.MaxValue;
                    }

                    if (_signal.Task.IsCompleted)
                    {
                        _signal = NewSignal();
                    }

                    signal = _signal.Task;
                }
            }

            if (ready is not null)
            {
                yield return ready;
                continue;
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = waitMs == Timeout.Infinite
                ? Task.Delay(Timeout.Infinite, delayCts.Token)
                : Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, waitMs)), _time, delayCts.Token);

            try
            {
                await Task.WhenAny(signal!, delay).ConfigureAwait(false);
            }
            finally
            {
                // Stops the timer when a signal won the race; cancellation of the delay itself is the normal shutdown path.
                delayCts.Cancel();
            }
        }
    }

    private void Schedule(Entry entry, long due)
    {
        entry.Version++;
        entry.DueMs = due;
        _schedule.Enqueue((entry, entry.Version), (due, ++_sequence));
    }

    private void SignalIfEarlier(long due)
    {
        if (due < _consumerWakeAt)
        {
            _consumerWakeAt = due;
            _signal.TrySetResult(true);
        }
    }

    private long NowMs() => _time.GetTimestamp() * 1000 / _time.TimestampFrequency;

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class Entry
    {
        internal Entry(string key, T payload, long firstSeenMs)
        {
            Key = key;
            Payload = payload;
            FirstSeenMs = firstSeenMs;
            ObservedLength = -1;
        }

        public string Key { get; }

        public T Payload { get; internal set; }

        /// <summary>How many times processing handed this entry back.</summary>
        public int Attempts { get; set; }

        public bool Deferred { get; internal set; }

        /// <summary>Length/mtime recorded by the previous readiness probe, or -1 when there is none yet.</summary>
        public long ObservedLength { get; set; }

        public long ObservedTicks { get; set; }

        internal long FirstSeenMs { get; set; }

        internal long DueMs { get; set; }

        internal int Version { get; set; }
    }
}
