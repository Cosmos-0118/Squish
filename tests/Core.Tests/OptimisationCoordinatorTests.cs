using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class OptimisationCoordinatorTests
{
    [Fact]
    public async Task ProcessesQueuedRequests()
    {
        var optimiser = new FakeOptimiser(ItemType.Image);
        await using var coordinator = new OptimisationCoordinator(new[] { optimiser });
        var request = new OptimisationRequest(ItemType.Image, CreateTempFile());

        var ticket = coordinator.Enqueue(request);
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Assert.Single(optimiser.ProcessedRequestIds, request.RequestId);
    }

    [Fact]
    public async Task RaisesProgressEvents()
    {
        var optimiser = new FakeOptimiser(ItemType.Image) { ReportProgress = true };
        await using var coordinator = new OptimisationCoordinator(new[] { optimiser });
        var events = new List<OptimisationProgress>();
        coordinator.ProgressChanged += (_, args) => events.Add(args.Progress);

        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Image, CreateTempFile()));
        _ = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(events, e => e.Percentage >= 50);
    }

    [Fact]
    public async Task CancelsRequestsViaToken()
    {
        var optimiser = new FakeOptimiser(ItemType.Image) { Delay = TimeSpan.FromSeconds(5) };
        await using var coordinator = new OptimisationCoordinator(new[] { optimiser });
        var cts = new CancellationTokenSource();

        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Image, CreateTempFile()), cts.Token);
        cts.CancelAfter(50);

        await Assert.ThrowsAsync<TaskCanceledException>(() => ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(OptimisationStatus.Cancelled, coordinator.GetStatus(ticket.RequestId));
    }

    [Fact]
    public async Task ReturnsUnsupportedWhenNoOptimiserRegistered()
    {
        await using var coordinator = new OptimisationCoordinator(Array.Empty<IOptimiser>());
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Image, CreateTempFile()));

        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Unsupported, result.Status);
    }

    private static FilePath CreateTempFile()
    {
        var file = FilePath.TempFile("optimiser-tests", ".tmp", addUniqueSuffix: true);
        file.EnsureParentDirectoryExists();
        File.WriteAllText(file.Value, "demo");
        return file;
    }

    private sealed class FakeOptimiser : IOptimiser
    {
        public FakeOptimiser(ItemType itemType)
        {
            ItemType = itemType;
        }

        public ItemType ItemType { get; }

        public List<string> ProcessedRequestIds { get; } = new();

        public bool ReportProgress { get; set; }

        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(10);

        public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            ProcessedRequestIds.Add(request.RequestId);
            if (ReportProgress)
            {
                context.ReportProgress(10, "initialising");
            }

            await Task.Delay(Delay, cancellationToken);

            if (ReportProgress)
            {
                context.ReportProgress(75, "working");
                context.ReportProgress(100, "done");
            }

            return OptimisationResult.Success(request.RequestId, request.SourcePath);
        }
    }
}
