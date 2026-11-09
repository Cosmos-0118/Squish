using System;
using System.Threading;
using System.Threading.Tasks;

namespace Squish.Core.Imaging;

/// <summary>
/// Bounds how many pixels the image pipeline decodes at once. One large picture already costs around a gigabyte to optimise
/// (decode, perceptual reference, candidates), so running several of them side by side multiplies that; small pictures,
/// which are the common case in documents, still run in parallel.
/// </summary>
internal sealed class PixelBudget
{
    /// <summary>
    /// The allowance every container job (PDF, Office) draws from. A budget per job meant N concurrent jobs could decode
    /// N times as many pictures; sharing one keeps the whole process under a single ceiling.
    /// </summary>
    public static PixelBudget Shared { get; } = new(12);

    private readonly int _capacity;
    private readonly SemaphoreSlim _units;
    // Permits are taken under this gate, so two large pictures cannot each hold half of what the other is waiting for.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="megapixels">Pictures in flight add up to at most this many megapixels (a larger picture still runs, alone).</param>
    public PixelBudget(int megapixels)
    {
        _capacity = Math.Max(1, megapixels);
        _units = new SemaphoreSlim(_capacity, _capacity);
    }

    public async Task<IDisposable> AcquireAsync(long pixels, CancellationToken token)
    {
        var weight = (int)Math.Clamp((pixels + 999_999) / 1_000_000, 1, _capacity);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var taken = 0;
        try
        {
            for (; taken < weight; taken++)
            {
                await _units.WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch
        {
            if (taken > 0)
            {
                _units.Release(taken);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }

        return new Lease(_units, weight);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _units;
        private readonly int _weight;

        public Lease(SemaphoreSlim units, int weight)
        {
            _units = units;
            _weight = weight;
        }

        public void Dispose() => Interlocked.Exchange(ref _units, null)?.Release(_weight);
    }
}
