using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Xunit;

namespace Core.Tests;

public class LossySearchTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Lowest = 40;
    private const int Highest = 95;
    private const int Budget = 7; // ImageOptimisationJob.SearchIterations

    /// <summary>The previous implementation: ceiling probe, then plain bisection. Kept as the reference to beat.</summary>
    private static async Task<(int? Found, int Calls)> Bisect(int lowest, int highest, double target, Func<int, double> curve, int iterations)
    {
        var calls = 0;
        double Eval(int q)
        {
            calls++;
            return curve(q);
        }

        await Task.Yield();
        if (lowest > highest || Eval(highest) < target)
        {
            return (null, calls);
        }

        var best = highest;
        var low = lowest;
        var high = highest - 1;
        while (low <= high && iterations-- > 0)
        {
            var mid = (low + high) / 2;
            if (Eval(mid) >= target)
            {
                best = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return (best, calls);
    }

    private static async Task<(int? Found, int Calls, List<int> Probes)> Search(int lowest, int highest, double target, Func<int, double?> curve, int iterations)
    {
        var probes = new List<int>();
        var found = await LossySearch.FindLowestPassingAsync(
            lowest, highest, target,
            (q, _) =>
            {
                probes.Add(q);
                return Task.FromResult(curve(q));
            },
            iterations,
            CancellationToken.None);
        return (found, probes.Count, probes);
    }

    private static int? BruteForce(int lowest, int highest, double target, Func<int, double> curve)
    {
        if (lowest > highest || curve(highest) < target)
        {
            return null;
        }

        for (var q = lowest; q <= highest; q++)
        {
            if (curve(q) >= target)
            {
                return q;
            }
        }

        return null;
    }

    /// <summary>Score model matching real encoders: ln(100 - score) is roughly linear in quality (slope ~0.03).</summary>
    private static Func<int, double> LogLinearCurve(double distortionAtTop, double slopePerQuality, double curvature = 0)
        => q => 100 - Math.Exp(Math.Log(distortionAtTop) + slopePerQuality * (Highest - q) + curvature * Math.Pow((Highest - q) / 55.0, 2));

    [Fact]
    public async Task FindsTheExactLowestPassingQualityOnRealisticCurves()
    {
        var rng = new Random(1234);
        var searchCalls = 0;
        var bisectCalls = 0;
        var worstSearch = 0;
        var cases = 0;

        for (var i = 0; i < 400; i++)
        {
            var curve = LogLinearCurve(
                distortionAtTop: 6 + rng.NextDouble() * 8,        // score 86-94 at q95
                slopePerQuality: 0.015 + rng.NextDouble() * 0.03,  // measured range 0.019-0.033
                curvature: (rng.NextDouble() - 0.5) * 0.4);
            var target = 60 + rng.NextDouble() * 22;                // 60-82 covers aggressive..strict targets

            var expected = BruteForce(Lowest, Highest, target, curve);
            var (found, calls, _) = await Search(Lowest, Highest, target, q => curve(q), Budget);
            var (bisected, bisectedCalls) = await Bisect(Lowest, Highest, target, curve, Budget);

            Assert.Equal(expected, found);
            Assert.Equal(bisected, found);
            Assert.True(calls <= 1 + Budget, $"{calls} probes exceeds the ceiling probe plus the budget");

            searchCalls += calls;
            bisectCalls += bisectedCalls;
            worstSearch = Math.Max(worstSearch, calls);
            cases++;
        }

        var average = searchCalls / (double)cases;
        var bisectAverage = bisectCalls / (double)cases;
        output.WriteLine($"average evaluations: score-aware {average:F2}, bisection {bisectAverage:F2}; worst case {worstSearch}");
        Assert.True(average <= 4.5, $"expected ~4 evaluations on average, measured {average:F2} (bisection {bisectAverage:F2})");
        Assert.True(average < bisectAverage - 1.5, $"expected at least 1.5 fewer evaluations than bisection: {average:F2} vs {bisectAverage:F2}");
    }

    [Fact]
    public async Task StaysWithinTheProbeBudgetAndStaysExactOnAdversarialCurves()
    {
        // Step functions and extremely convex/concave responses defeat interpolation; the projection must still finish.
        var curves = new List<Func<int, double>>();
        for (var step = Lowest; step <= Highest; step += 3)
        {
            var s = step;
            curves.Add(q => q >= s ? 90 : 10);                          // cliff
            curves.Add(q => 100 - 60 * Math.Pow((Highest - q) / 55.0 + 0.001, 4) - (q >= s ? 0 : 20)); // very convex + cliff
            curves.Add(q => 40 + 55 * Math.Pow(q / (double)Highest, 12)); // very concave
        }

        foreach (var curve in curves)
        {
            foreach (var target in new[] { 55.0, 68.0, 74.0, 85.0 })
            {
                var expected = BruteForce(Lowest, Highest, target, curve);
                var (found, calls, _) = await Search(Lowest, Highest, target, q => curve(q), Budget);
                var (bisected, bisectedCalls) = await Bisect(Lowest, Highest, target, curve, Budget);

                Assert.Equal(bisected, found);
                Assert.Equal(expected, found);
                // The guarantee is the caller's budget (ceiling probe + Budget), the same hard cap the bisection ran under.
                Assert.True(calls <= 1 + Budget, $"{calls} probes exceeds the cap; bisection used {bisectedCalls} (target {target})");
            }
        }
    }

    [Fact]
    public async Task ABudgetTooSmallToBisectStillYieldsAPassingQualityAndBeatsBisection()
    {
        // The AVIF search runs on 5 iterations: bisection cannot resolve 55 qualities with that and returns a conservative
        // (too high) answer, while a score-aware search lands on the exact quality.
        var rng = new Random(99);
        var exactSearch = 0;
        var exactBisect = 0;
        const int cases = 200;

        for (var i = 0; i < cases; i++)
        {
            var curve = LogLinearCurve(6 + rng.NextDouble() * 8, 0.02 + rng.NextDouble() * 0.02);
            var target = 62 + rng.NextDouble() * 18;
            var expected = BruteForce(Lowest, Highest, target, curve);

            var (found, calls, _) = await Search(Lowest, Highest, target, q => curve(q), 5);
            var (bisected, _) = await Bisect(Lowest, Highest, target, curve, 5);

            Assert.True(calls <= 6);
            if (expected is null)
            {
                Assert.Null(found);
                continue;
            }

            Assert.NotNull(found);
            Assert.True(curve(found.Value) >= target, "a returned quality must always meet the target");
            Assert.True(found >= expected);
            Assert.True(found <= bisected, $"search {found} should be no worse than bisection {bisected}");
            exactSearch += found == expected ? 1 : 0;
            exactBisect += bisected == expected ? 1 : 0;
        }

        Assert.True(exactSearch > exactBisect, $"exact answers: search {exactSearch}, bisection {exactBisect}");
    }

    [Fact]
    public async Task ToleratesNoisyNonMonotonicScores()
    {
        // Encoder noise makes neighbouring qualities swap order; the answer must still be a quality that really passed.
        var rng = new Random(7);
        for (var i = 0; i < 300; i++)
        {
            var baseCurve = LogLinearCurve(8, 0.025);
            var noise = new Dictionary<int, double>();
            for (var q = Lowest; q <= Highest; q++)
            {
                noise[q] = (rng.NextDouble() - 0.5) * 1.2;
            }

            double Noisy(int q) => baseCurve(q) + noise[q];
            var target = 64 + rng.NextDouble() * 16;
            var (found, calls, probes) = await Search(Lowest, Highest, target, q => Noisy(q), Budget);

            Assert.True(calls <= 1 + Budget);
            if (found is { } quality)
            {
                Assert.Contains(quality, probes);
                Assert.True(Noisy(quality) >= target);
            }
            else
            {
                Assert.True(Noisy(Highest) < target);
            }
        }
    }

    [Fact]
    public async Task FailedEncodesAreTreatedAsFailingQualities()
    {
        var curve = LogLinearCurve(8, 0.025);
        var target = 74.0;
        var expected = BruteForce(Lowest, Highest, target, curve)!.Value;
        var broken = expected - 1;

        // Qualities below the answer cannot be encoded at all (null) and must simply count as misses.
        var (found, _, _) = await Search(Lowest, Highest, target, q => q <= broken ? null : curve(q), Budget);

        Assert.Equal(expected, found);
    }

    [Fact]
    public async Task ReturnsTheFloorWhenEveryQualityPasses()
    {
        var (found, calls, _) = await Search(Lowest, Highest, 50, _ => 99, Budget);

        Assert.Equal(Lowest, found);
        Assert.True(calls <= 1 + Budget);
    }

    [Fact]
    public async Task SingleQualityWindowOnlyProbesTheCeiling()
    {
        var (found, calls, _) = await Search(80, 80, 70, _ => 75, Budget);

        Assert.Equal(80, found);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EmptyWindowFindsNothing()
    {
        var (found, calls, _) = await Search(90, 80, 70, _ => 99, Budget);

        Assert.Null(found);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => LossySearch.FindLowestPassingAsync(
            Lowest, Highest, 74,
            (q, token) =>
            {
                calls++;
                cts.Cancel();
                return Task.FromResult<double?>(q - 10);
            },
            Budget,
            cts.Token));

        Assert.Equal(1, calls);
    }
}
