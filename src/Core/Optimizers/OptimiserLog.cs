using System;

namespace ClopWindows.Core.Optimizers;

internal static class OptimiserLog
{
    public static object BuildContext(OptimisationRequest request)
    {
        return new
        {
            request.RequestId,
            ItemType = request.ItemType.ToString(),
            Source = request.SourcePath.Value,
            Metadata = request.Metadata
        };
    }

    public static string BuildErrorMessage(string operation, Exception ex)
    {
        return $"{operation} failed: {ex}";
    }
}
