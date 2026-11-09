using System;
using System.Threading;
using System.Threading.Tasks;

namespace Squish.Core.Optimizers;

/// <summary>
/// Wraps the video optimiser so options refresh from settings on every request, as <see cref="SettingsBackedImageOptimiser"/>
/// does for images.
/// </summary>
public sealed class SettingsBackedVideoOptimiser : IOptimiser
{
    private readonly Func<VideoOptimiserOptions> _optionsFactory;

    public SettingsBackedVideoOptimiser(Func<VideoOptimiserOptions> optionsFactory)
    {
        _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
    }

    public ItemType ItemType => ItemType.Video;

    public Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var options = _optionsFactory();
        var optimiser = new VideoOptimiser(options);
        return optimiser.OptimiseAsync(request, context, cancellationToken);
    }
}
