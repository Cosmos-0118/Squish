using System;

namespace Squish.Core.Shared;

/// <summary>
/// Helper routines for identifying files that Squish already produced so automation flows can avoid
/// re-processing the same content repeatedly.
/// </summary>
public static class SquishFileGuards
{
    private static readonly string[] OutputSuffixes = { ".squish", ".clop" };

    /// <summary>
    /// Returns true when the provided file follows Squish's output naming pattern
    /// ("*.squish" or "*.squish.*", plus the "*.clop" names written before the rename).
    /// </summary>
    public static bool IsSquishGenerated(FilePath path)
    {
        var fileName = path.Name;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        // Office keeps a hidden "~$name.docx" next to every open document. It is not a document and must never be touched.
        if (fileName.StartsWith("~$", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var suffix in OutputSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(suffix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
