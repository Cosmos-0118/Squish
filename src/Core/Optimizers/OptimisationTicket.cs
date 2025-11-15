namespace ClopWindows.Core.Optimizers;

public sealed class OptimisationTicket
{
    internal OptimisationTicket(string requestId, Task<OptimisationResult> completion)
    {
        RequestId = requestId;
        Completion = completion;
    }

    public string RequestId { get; }

    public Task<OptimisationResult> Completion { get; }
}
