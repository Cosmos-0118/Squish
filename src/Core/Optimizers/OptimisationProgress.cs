namespace ClopWindows.Core.Optimizers;

public sealed record OptimisationProgress(string RequestId, double Percentage, string? Message = null)
{
    public static OptimisationProgress None(string requestId) => new(requestId, 0d, "Queued");
}
