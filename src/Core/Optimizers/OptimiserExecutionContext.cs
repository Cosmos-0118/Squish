namespace ClopWindows.Core.Optimizers;

public sealed class OptimiserExecutionContext
{
    private readonly Action<OptimisationProgress> _progressReporter;

    internal OptimiserExecutionContext(string requestId, Action<OptimisationProgress> progressReporter)
    {
        RequestId = requestId;
        _progressReporter = progressReporter;
    }

    public string RequestId { get; }

    public void ReportProgress(double percentage, string? message = null)
    {
        var clamped = Math.Clamp(percentage, 0d, 100d);
        _progressReporter(new OptimisationProgress(RequestId, clamped, message));
    }
}
