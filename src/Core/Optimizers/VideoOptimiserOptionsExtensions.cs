using System;

namespace ClopWindows.Core.Optimizers;

/// <summary>
/// Convenience helpers for wiring <see cref="VideoOptimiserOptions"/> from hosts/CLI layers.
/// </summary>
public static class VideoOptimiserOptionsExtensions
{
    /// <summary>
    /// Pins hardware capabilities so that the optimiser can skip repeated probing and always attempt a GPU encode when available.
    /// </summary>
    /// <remarks>
    /// Hosts should call this once up-front (for example, during service registration) and reuse the returned options instance.
    /// </remarks>
    public static VideoOptimiserOptions WithHardwareOverride(this VideoOptimiserOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (!options.UseHardwareAcceleration)
        {
            return options;
        }

        if (options.HardwareOverride is { HasAnyHardware: true })
        {
            return options;
        }

        if (!options.ProbeHardwareCapabilities)
        {
            return options;
        }

        var capabilities = VideoHardwareDetector.Detect(options.FfmpegPath, probe: true);
        return options with
        {
            HardwareOverride = capabilities,
            // Once we have an override we no longer need to probe for each request.
            ProbeHardwareCapabilities = false
        };
    }
}
