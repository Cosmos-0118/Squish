using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public sealed record OptimisationResult(
    string RequestId,
    OptimisationStatus Status,
    FilePath? OutputPath = null,
    string? Message = null,
    TimeSpan? Duration = null)
{
    public static OptimisationResult Success(string requestId, FilePath output, TimeSpan? duration = null) =>
        new(requestId, OptimisationStatus.Succeeded, output, null, duration);

    public static OptimisationResult Failure(string requestId, string? message = null) =>
        new(requestId, OptimisationStatus.Failed, null, message);

    public static OptimisationResult Cancelled(string requestId) =>
        new(requestId, OptimisationStatus.Cancelled);

    public static OptimisationResult Unsupported(string requestId) =>
        new(requestId, OptimisationStatus.Unsupported, null, "No optimiser registered for item type.");
}
