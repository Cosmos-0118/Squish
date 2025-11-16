using System;
using System.Collections.Generic;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

internal static class OptimisedOutputPlanner
{
    public static OutputPathPlan Plan(
        FilePath source,
        string finalExtension,
        IReadOnlyDictionary<string, object?> metadata,
        Func<FilePath, string, FilePath> defaultBuilder)
    {
        if (defaultBuilder is null)
        {
            throw new ArgumentNullException(nameof(defaultBuilder));
        }

        if (!OptimisationMetadata.GetFlag(metadata, OptimisationMetadata.OutputReplaceOriginal))
        {
            return new OutputPathPlan(defaultBuilder(source, finalExtension), false);
        }

        var sourceExtension = source.Extension;
        if (!string.IsNullOrWhiteSpace(sourceExtension) &&
            string.Equals(sourceExtension, finalExtension, StringComparison.OrdinalIgnoreCase))
        {
            return new OutputPathPlan(source, false);
        }

        var destination = source.Parent.Append($"{source.Stem}.{finalExtension}");
        var deleteSource = OptimisationMetadata.GetFlag(metadata, OptimisationMetadata.OutputDeleteConvertedSource);
        return new OutputPathPlan(destination, deleteSource);
    }
}

internal readonly record struct OutputPathPlan(FilePath Destination, bool DeleteSource)
{
    public bool RequiresSourceDeletion(FilePath source)
    {
        return DeleteSource && !string.Equals(source.Value, Destination.Value, StringComparison.OrdinalIgnoreCase);
    }
}
