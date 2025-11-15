using System.Collections.Generic;

namespace ClopWindows.Core.Shared;

public static class OptimisationMetadata
{
    public const string OutputReplaceOriginal = "output.replaceOriginal";
    public const string OutputDeleteConvertedSource = "output.deleteConvertedSource";

    public static bool GetFlag(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && value is bool flag && flag;
    }
}
