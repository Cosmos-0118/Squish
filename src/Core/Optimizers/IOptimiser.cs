namespace ClopWindows.Core.Optimizers;

public interface IOptimiser
{
    ItemType ItemType { get; }

    Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken);
}
