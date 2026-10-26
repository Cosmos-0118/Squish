using System;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Shared;

namespace Squish.Core.Imaging;

/// <summary>An encoded candidate together with the quality setting and perceptual score that produced it.</summary>
internal sealed record EncodedCandidate(FilePath File, int Quality, double Score)
{
    /// <summary>Which encoder produced the file, for the result description.</summary>
    public string? Encoder { get; init; }
}

/// <summary>
/// Finds the <em>lowest</em> quality setting whose output still meets a perceptual target. Quality-to-score is
/// (nearly) monotonic for every codec we drive, so a bisection needs only ~6 evaluations instead of a fixed,
/// content-blind quality number.
/// </summary>
internal static class LossySearch
{
    /// <summary>
    /// Evaluates <paramref name="evaluate"/> over [<paramref name="lowest"/>, <paramref name="highest"/>].
    /// The callback encodes at the given quality and returns the score (or null when encoding failed).
    /// Files for rejected qualities are released through <paramref name="discard"/>.
    /// </summary>
    public static async Task<int?> FindLowestPassingAsync(
        int lowest,
        int highest,
        double targetScore,
        Func<int, CancellationToken, Task<double?>> evaluate,
        int maxIterations,
        CancellationToken token)
    {
        if (lowest > highest)
        {
            return null;
        }

        // The ceiling must work at all, otherwise nothing in the window will.
        var top = await evaluate(highest, token).ConfigureAwait(false);
        if (top is null || top.Value < targetScore)
        {
            return null;
        }

        var best = highest;
        var low = lowest;
        var high = highest - 1;
        var iterations = Math.Max(1, maxIterations);

        while (low <= high && iterations-- > 0)
        {
            token.ThrowIfCancellationRequested();

            var mid = (low + high) / 2;
            var score = await evaluate(mid, token).ConfigureAwait(false);
            if (score is { } value && value >= targetScore)
            {
                best = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return best;
    }
}
