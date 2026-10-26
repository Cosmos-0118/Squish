using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Squish.Core.Optimizers;

/// <summary>Measured outcome of encoding the sample clips at one CRF.</summary>
internal readonly record struct CrfProbe(int Crf, double Score, double BitrateKbps);

internal sealed record CrfSearchOutcome(CrfProbe Chosen, bool TargetMet, IReadOnlyList<CrfProbe> Probes);

/// <summary>
/// Content-adaptive constant-quality selection ("ab-av1" style): finds the <em>highest</em> CRF (smallest file)
/// whose sampled quality still meets the target. Quality falls almost linearly with CRF over the useful range, so a
/// slope-guided secant converges in two or three probes instead of a blind bisection over the whole range.
/// </summary>
internal static class CrfSearch
{
    public static async Task<CrfSearchOutcome?> FindAsync(
        int startCrf,
        int minCrf,
        int maxCrf,
        double targetScore,
        double priorSlopePerCrf,
        Func<int, CancellationToken, Task<CrfProbe?>> probe,
        int maxProbes,
        CancellationToken token,
        double tolerance = 2.0)
    {
        if (minCrf > maxCrf)
        {
            return null;
        }

        var measured = new List<CrfProbe>();
        CrfProbe? bestPassing = null;      // highest CRF that met the target
        CrfProbe? lowestFailing = null;    // lowest CRF that missed it
        var next = Math.Clamp(startCrf, minCrf, maxCrf);

        for (var i = 0; i < maxProbes; i++)
        {
            token.ThrowIfCancellationRequested();

            var result = await probe(next, token).ConfigureAwait(false);
            if (result is null)
            {
                // Encoder or scorer failed: no basis for a decision.
                return measured.Count == 0 ? null : Conclude(measured, bestPassing, targetScore);
            }

            measured.Add(result.Value);
            if (result.Value.Score >= targetScore)
            {
                if (bestPassing is null || result.Value.Crf > bestPassing.Value.Crf)
                {
                    bestPassing = result.Value;
                }
            }
            else if (lowestFailing is null || result.Value.Crf < lowestFailing.Value.Crf)
            {
                lowestFailing = result.Value;
            }

            // Converged: adjacent CRFs straddle the target...
            if (bestPassing is { } pass && lowestFailing is { } fail && fail.Crf - pass.Crf <= 1)
            {
                break;
            }

            // ...or a passing probe already sits within the tolerance band above it (each CRF step is worth a few
            // percent of bitrate; squeezing out the last step is not worth another sampled encode).
            if (result.Value.Score >= targetScore && result.Value.Score - targetScore <= tolerance)
            {
                break;
            }

            var candidate = PredictNext(result.Value, bestPassing, lowestFailing, targetScore, priorSlopePerCrf, minCrf, maxCrf);
            if (candidate is null || measured.Exists(m => m.Crf == candidate.Value))
            {
                break;
            }

            next = candidate.Value;
        }

        return Conclude(measured, bestPassing, targetScore);
    }

    /// <summary>
    /// Quality-first can go too far on grainy footage: if the chosen CRF still costs more than <paramref name="capKbps"/>
    /// the CRF is raised (bitrate decays roughly exponentially with CRF) until the cap holds, accepting a score below target.
    /// </summary>
    public static async Task<CrfSearchOutcome> EnforceBitrateCapAsync(
        CrfSearchOutcome outcome,
        double capKbps,
        int maxCrf,
        Func<int, CancellationToken, Task<CrfProbe?>> probe,
        CancellationToken token,
        int maxExtraProbes = 2,
        double minScore = double.NegativeInfinity)
    {
        var chosen = outcome.Chosen;
        var probes = new List<CrfProbe>(outcome.Probes);
        var targetMet = outcome.TargetMet;
        var adjusted = false;

        for (var i = 0; i < maxExtraProbes && chosen.BitrateKbps > capKbps && chosen.Crf < maxCrf; i++)
        {
            token.ThrowIfCancellationRequested();

            var decayPerCrf = EstimateDecay(probes);
            var estimated = chosen.Crf + (int)Math.Ceiling(Math.Log(chosen.BitrateKbps / capKbps) / decayPerCrf);
            estimated = Math.Clamp(estimated, chosen.Crf + 1, maxCrf);

            var result = await probe(estimated, token).ConfigureAwait(false);
            if (result is null)
            {
                break;
            }

            probes.Add(result.Value);

            if (result.Value.Score < minScore)
            {
                // The cap went too far. Try once half way; otherwise keep the last acceptable choice.
                var half = chosen.Crf + (estimated - chosen.Crf) / 2;
                if (half <= chosen.Crf || half >= estimated)
                {
                    break;
                }

                var middle = await probe(half, token).ConfigureAwait(false);
                if (middle is null)
                {
                    break;
                }

                probes.Add(middle.Value);
                if (middle.Value.Score >= minScore)
                {
                    chosen = middle.Value;
                    adjusted = true;
                }

                break;
            }

            chosen = result.Value;
            adjusted = true;
        }

        // A cap-forced CRF trades the quality target for a sane file size, so the target no longer counts as met.
        return new CrfSearchOutcome(chosen, targetMet && !adjusted, probes);
    }

    private static double EstimateDecay(IReadOnlyList<CrfProbe> probes)
    {
        // ln(bitrate) falls ~0.10-0.12 per CRF step for x264/x265; use measured pairs when available.
        double best = 0.11;
        for (var i = 0; i < probes.Count; i++)
        {
            for (var j = i + 1; j < probes.Count; j++)
            {
                var a = probes[i];
                var b = probes[j];
                if (a.Crf != b.Crf && a.BitrateKbps > 0 && b.BitrateKbps > 0)
                {
                    var measured = Math.Log(a.BitrateKbps / b.BitrateKbps) / (b.Crf - a.Crf);
                    if (measured is > 0.03 and < 0.4)
                    {
                        best = measured;
                    }
                }
            }
        }

        return best;
    }

    private static int? PredictNext(CrfProbe last, CrfProbe? bestPassing, CrfProbe? lowestFailing, double target, double priorSlope, int minCrf, int maxCrf)
    {
        double slope = priorSlope; // score change per CRF step (negative)
        if (bestPassing is { } p && lowestFailing is { } f && f.Crf != p.Crf)
        {
            slope = (f.Score - p.Score) / (f.Crf - p.Crf);
        }

        if (slope > -0.05)
        {
            slope = -0.05;
        }

        // Passing probes jump to the nearest predicted CRF (the next probe verifies it); failing probes round towards
        // better quality so the search backs off conservatively.
        var steps = (target - last.Score) / slope;
        var wanted = last.Crf + (steps >= 0 ? (int)Math.Round(steps, MidpointRounding.AwayFromZero) : (int)Math.Floor(steps));
        if (last.Score >= target && wanted <= last.Crf)
        {
            wanted = last.Crf + 1;
        }

        if (bestPassing is { } pass && lowestFailing is { } fail)
        {
            wanted = Math.Clamp(wanted, pass.Crf + 1, fail.Crf - 1);
        }
        else if (lowestFailing is { } onlyFail)
        {
            wanted = Math.Min(wanted, onlyFail.Crf - 1);
        }
        else if (bestPassing is { } onlyPass)
        {
            wanted = Math.Max(wanted, onlyPass.Crf + 1);
        }

        wanted = Math.Clamp(wanted, minCrf, maxCrf);
        return wanted == last.Crf ? null : wanted;
    }

    private static CrfSearchOutcome Conclude(List<CrfProbe> measured, CrfProbe? bestPassing, double target)
    {
        if (bestPassing is { } pass)
        {
            return new CrfSearchOutcome(pass, true, measured);
        }

        // Nothing reached the target (grainy or very detailed footage): fall back to the best quality we measured,
        // which is the lowest CRF probed.
        var lowest = measured[0];
        foreach (var entry in measured)
        {
            if (entry.Crf < lowest.Crf)
            {
                lowest = entry;
            }
        }

        _ = target;
        return new CrfSearchOutcome(lowest, false, measured);
    }
}
