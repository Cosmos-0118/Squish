using Squish.BackgroundService.Automation;
using Xunit;

namespace BackgroundService.Tests;

public sealed class FileSettleQueueTests
{
    private static async Task<List<FileSettleQueue<int>.Entry>> ReadAsync(FileSettleQueue<int> queue, int count, TimeSpan timeout)
    {
        var results = new List<FileSettleQueue<int>.Entry>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var entry in queue.ReadDueAsync(cts.Token))
            {
                results.Add(entry);
                if (results.Count >= count)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return results;
    }

    [Fact]
    public async Task ReleasesEntryOnlyAfterQuietPeriod()
    {
        var queue = new FileSettleQueue<int>();
        var started = DateTime.UtcNow;
        queue.Touch("a", 1, TimeSpan.FromMilliseconds(150));

        var results = await ReadAsync(queue, 1, TimeSpan.FromSeconds(5));

        Assert.Single(results);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(140));
    }

    [Fact]
    public async Task RepeatedTouchesCoalesceIntoOneEntryAndPushBackTheDeadline()
    {
        var queue = new FileSettleQueue<int>(merge: (_, latest) => latest);
        var started = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            queue.Touch("a", i, TimeSpan.FromMilliseconds(120));
            await Task.Delay(60);
        }

        var results = await ReadAsync(queue, 2, TimeSpan.FromSeconds(1));

        Assert.Single(results);
        Assert.Equal(4, results[0].Payload);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(400));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task KeysAreCaseInsensitive()
    {
        var queue = new FileSettleQueue<int>();
        queue.Touch(@"C:\Pics\A.JPG", 1, TimeSpan.FromMilliseconds(20));
        queue.Touch(@"c:\pics\a.jpg", 2, TimeSpan.FromMilliseconds(20));

        var results = await ReadAsync(queue, 2, TimeSpan.FromMilliseconds(600));

        Assert.Single(results);
    }

    [Fact]
    public async Task RescheduledEntryDoesNotBlockReadyEntries()
    {
        var queue = new FileSettleQueue<int>();
        queue.Touch("slow", 1, TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var order = new List<string>();
        await foreach (var entry in queue.ReadDueAsync(cts.Token))
        {
            order.Add(entry.Key);
            if (entry.Key == "slow" && entry.Attempts == 0)
            {
                entry.Attempts++;
                queue.Reschedule(entry, TimeSpan.FromMilliseconds(400));
                queue.Touch("fast", 2, TimeSpan.FromMilliseconds(10));
                continue;
            }

            if (order.Count >= 3)
            {
                break;
            }
        }

        Assert.Equal(new[] { "slow", "fast", "slow" }, order);
    }

    [Fact]
    public async Task WakeDeferredMakesDeferredEntriesDueImmediately()
    {
        var queue = new FileSettleQueue<int>();
        queue.Touch("a", 1, TimeSpan.FromMilliseconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = queue.ReadDueAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await enumerator.MoveNextAsync());
        queue.Reschedule(enumerator.Current, TimeSpan.FromMinutes(5), deferred: true);

        var next = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100);
        Assert.False(next.IsCompleted);

        queue.WakeDeferred();
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("a", enumerator.Current.Key);
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task TouchWhileConsumerIsIdleWakesIt()
    {
        var queue = new FileSettleQueue<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = Task.Run(async () =>
        {
            await foreach (var entry in queue.ReadDueAsync(cts.Token))
            {
                return entry.Key;
            }

            return null;
        });

        await Task.Delay(100);
        queue.Touch("late", 1, TimeSpan.FromMilliseconds(20));

        Assert.Equal("late", await reader.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task HandlesManyConcurrentProducers()
    {
        var queue = new FileSettleQueue<int>();
        const int producers = 8;
        const int perProducer = 2000;

        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                queue.Touch($"file-{p}-{i}", i, TimeSpan.FromMilliseconds(30));
            }
        })));

        var results = await ReadAsync(queue, producers * perProducer, TimeSpan.FromSeconds(20));

        Assert.Equal(producers * perProducer, results.Count);
        Assert.Equal(results.Count, results.Select(r => r.Key).Distinct().Count());
    }
}
