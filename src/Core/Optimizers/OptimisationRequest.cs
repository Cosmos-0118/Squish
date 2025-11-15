using System.Collections.Immutable;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public sealed class OptimisationRequest
{
    public OptimisationRequest(ItemType itemType, FilePath sourcePath, string? requestId = null, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ItemType = itemType;
        SourcePath = sourcePath;
        RequestId = string.IsNullOrWhiteSpace(requestId) ? NanoId.New(12) : requestId!;
        Metadata = metadata is null ? ImmutableDictionary<string, object?>.Empty : metadata.ToImmutableDictionary(StringComparer.Ordinal);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string RequestId { get; }

    public ItemType ItemType { get; }

    public FilePath SourcePath { get; }

    public IReadOnlyDictionary<string, object?> Metadata { get; }

    public DateTimeOffset CreatedAt { get; }
}
