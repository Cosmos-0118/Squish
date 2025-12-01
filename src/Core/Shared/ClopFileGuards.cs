using System;

namespace ClopWindows.Core.Shared;

/// <summary>
/// Helper routines for identifying files that Clop already produced so automation flows can avoid
/// re-processing the same content repeatedly.
/// </summary>
public static class ClopFileGuards
{
    /// <summary>
    /// Returns true when the provided file follows Clop's output naming pattern
    /// ("*.clop" or "*.clop.*").
    /// </summary>
    public static bool IsClopGenerated(FilePath path)
    {
        var fileName = path.Name;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.EndsWith(".clop", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.Contains(".clop.", StringComparison.OrdinalIgnoreCase);
    }
}
