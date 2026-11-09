using System;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Documents;

namespace Squish.Core.Optimizers;

/// <summary>Wraps the document optimiser so options refresh from settings on every request, like the image and video wrappers.</summary>
public sealed class SettingsBackedDocumentOptimiser : IOptimiser
{
    private readonly Func<DocumentOptimiserOptions> _optionsFactory;

    public SettingsBackedDocumentOptimiser(Func<DocumentOptimiserOptions> optionsFactory)
    {
        _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
    }

    public ItemType ItemType => ItemType.Document;

    public Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var optimiser = new DocumentOptimiser(_optionsFactory());
        return optimiser.OptimiseAsync(request, context, cancellationToken);
    }
}
