using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Squish.BackgroundService;

/// <summary>Restart policy for <see cref="ServiceSupervisor"/>.</summary>
public sealed record SupervisorOptions
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A run that lasts at least this long counts as healthy and resets the failure streak.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(60);

    public static SupervisorOptions Default { get; } = new();
}

/// <summary>
/// Keeps a long-running background loop alive. If it throws, or returns while the host is still running, it is
/// restarted with exponential backoff, so one faulty subsystem can neither take the host down nor stay dead
/// quietly while the others keep running.
/// </summary>
public static class ServiceSupervisor
{
    public static async Task RunAsync(
        string name,
        Func<CancellationToken, Task> run,
        ILogger logger,
        CancellationToken cancellationToken,
        SupervisorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(logger);

        options ??= SupervisorOptions.Default;
        var time = timeProvider ?? TimeProvider.System;
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var started = time.GetTimestamp();

            try
            {
                await run(cancellationToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                logger.LogWarning("Background service '{Service}' stopped unexpectedly; restarting.", name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background service '{Service}' crashed; restarting.", name);
            }

            if (time.GetElapsedTime(started) >= options.StableAfter)
            {
                consecutiveFailures = 0;
            }

            consecutiveFailures++;
            var delay = BackoffFor(consecutiveFailures, options);
            logger.LogInformation("Restarting '{Service}' in {Delay} (failure #{Count}).", name, delay, consecutiveFailures);

            try
            {
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal static TimeSpan BackoffFor(int consecutiveFailures, SupervisorOptions options)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 20);
        var ticks = options.InitialDelay.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(ticks, options.MaxDelay.Ticks));
    }
}
