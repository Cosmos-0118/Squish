using System;
using System.Collections.Generic;
using System.Linq;
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
/// (nearly) monotonic for every codec we drive, so a search needs only a handful of evaluations instead of a fixed,
/// content-blind quality number.
/// </summary>
/// <remarks>
/// The search is score-aware rather than a blind bisection: the distortion <c>100 - score</c> follows a power law of
/// the quantiser, so <c>ln(100 - score)</c> is close to linear in quality (measured: ~0.03 per quality step for JPEG
/// between q40 and q95). Each probe is therefore placed where an interpolation of the scores measured so far predicts
/// the target to be crossed (regula falsi in that log-distortion space; extrapolation along the measured slope until
/// the target has been bracketed). A projection step in the spirit of Oliveira &amp; Takahashi's ITP method keeps every
/// probe inside the range from which plain bisection could still finish within the iteration budget, so a badly
/// predicted probe can never make the search worse than bisection would have been. The result is the same lowest
/// passing quality, found in roughly half the evaluations.
/// </remarks>
internal static class LossySearch
{
    // ln-distortion rise per quality step when no slope has been measured yet (see the remarks); clamped when estimated.
    private const double PriorSlope = 0.03;
    private const double MinSlope = 0.012;
    private const double MaxSlope = 0.12;

    /// <summary>
    /// Evaluates <paramref name="evaluate"/> over [<paramref name="lowest"/>, <paramref name="highest"/>].
    /// The callback encodes at the given quality and returns the score (or null when encoding failed).
    /// <paramref name="maxIterations"/> bounds the evaluations after the ceiling probe, which is always made.
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

        var targetDistortion = Distortion(targetScore);

        // Invariant: `pass` meets the target, `fail` (when it really was measured) does not, and the answer lies in (fail, pass].
        var pass = highest;
        var passDistortion = Distortion(top.Value);
        var fail = lowest - 1;                 // virtual until a probe fails: nothing below `lowest` is ever evaluated
        var failDistortion = double.NaN;       // NaN: unknown (virtual, or the encode failed and gave no score)
        var measured = new List<(int Quality, double Distortion)> { (highest, passDistortion) };
        var budget = Math.Max(1, maxIterations);

        while (pass - fail > 1 && budget > 0)
        {
            token.ThrowIfCancellationRequested();

            var next = ChooseNext(fail, failDistortion, pass, passDistortion, targetDistortion, measured, budget);
            budget--;

            var score = await evaluate(next, token).ConfigureAwait(false);
            if (score is { } value)
            {
                measured.Add((next, Distortion(value)));
            }

            if (score is { } passing && passing >= targetScore)
            {
                pass = next;
                passDistortion = Distortion(passing);
            }
            else
            {
                fail = next;
                failDistortion = score is { } failing ? Distortion(failing) : double.NaN;
            }
        }

        return pass;
    }

    /// <summary>ln of the remaining distortion; the floor keeps near-perfect scores (and scores above 100) finite.</summary>
    private static double Distortion(double score) => Math.Log(Math.Max(0.5, 100.0 - score));

    private static int ChooseNext(
        int fail,
        double failDistortion,
        int pass,
        double passDistortion,
        double targetDistortion,
        List<(int Quality, double Distortion)> measured,
        int budget)
    {
        var span = pass - fail;                // >= 2: there is at least one untested quality in between
        var middle = fail + span / 2;

        double estimate;
        if (!double.IsNaN(failDistortion) && failDistortion - passDistortion > 1e-9)
        {
            // Bracketed by real measurements: regula falsi on the (near-linear) log distortion.
            estimate = fail + (failDistortion - targetDistortion) / (failDistortion - passDistortion) * span;
        }
        else if (!double.IsNaN(failDistortion))
        {
            estimate = middle;                 // flat or inconsistent response: no information, fall back to bisection
        }
        else
        {
            // Only passing probes so far: extrapolate downwards along the slope they show (or the prior).
            estimate = pass - Math.Max(0, targetDistortion - passDistortion) / EstimateSlope(measured);
        }

        // The lowest passing quality is the first integer at or above the predicted crossing.
        var next = double.IsFinite(estimate) ? (int)Math.Ceiling(Math.Clamp(estimate, fail, pass) - 1e-6) : middle;

        // Keep the probe inside the window from which bisection could still finish within the budget: after it, the larger
        // side may be at most 2^(budget-1) wide. With budget to spare this leaves room to follow the interpolation; with
        // none it degenerates to the midpoint. A budget too small to bisect at all is left unconstrained.
        if (budget <= 30)
        {
            var reach = 1L << (budget - 1);
            if (span <= 2 * reach)
            {
                next = (int)Math.Clamp(next, Math.Max(fail + 1, pass - reach), Math.Min(pass - 1, fail + reach));
            }
        }

        return Math.Clamp(next, fail + 1, pass - 1);
    }

    /// <summary>Slope of ln-distortion per quality step, measured from the two lowest-quality points seen so far.</summary>
    private static double EstimateSlope(List<(int Quality, double Distortion)> measured)
    {
        if (measured.Count >= 2)
        {
            var ordered = measured.OrderBy(m => m.Quality).Take(2).ToArray();
            var dq = ordered[1].Quality - ordered[0].Quality;
            if (dq >= 2)
            {
                var slope = (ordered[0].Distortion - ordered[1].Distortion) / dq;
                if (double.IsFinite(slope))
                {
                    return Math.Clamp(slope, MinSlope, MaxSlope);
                }
            }
        }

        return PriorSlope;
    }
}
