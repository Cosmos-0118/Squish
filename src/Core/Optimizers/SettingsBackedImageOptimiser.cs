using System;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Settings;

namespace ClopWindows.Core.Optimizers;

/// <summary>
/// Wraps the image optimiser so options refresh from settings on every request.
/// </summary>
public sealed class SettingsBackedImageOptimiser : IOptimiser
{
    private readonly Func<ImageOptimiserOptions> _optionsFactory;

    public SettingsBackedImageOptimiser(Func<ImageOptimiserOptions> optionsFactory)
    {
        _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
    }

    public ItemType ItemType => ItemType.Image;

    public Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var options = _optionsFactory();
        var optimiser = new ImageOptimiser(options);
        return optimiser.OptimiseAsync(request, context, cancellationToken);
    }
}
