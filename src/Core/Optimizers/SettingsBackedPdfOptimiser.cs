using System;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Pdf;

namespace Squish.Core.Optimizers;

/// <summary>Wraps the PDF optimiser so options refresh from settings on every request, like the other settings-backed optimisers.</summary>
public sealed class SettingsBackedPdfOptimiser : IOptimiser
{
    private readonly Func<PdfOptimiserOptions> _optionsFactory;

    public SettingsBackedPdfOptimiser(Func<PdfOptimiserOptions> optionsFactory)
    {
        _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
    }

    public ItemType ItemType => ItemType.Pdf;

    public Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var optimiser = new PdfOptimiser(_optionsFactory());
        return optimiser.OptimiseAsync(request, context, cancellationToken);
    }
}
