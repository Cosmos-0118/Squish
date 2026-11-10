using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Processes;
using Squish.Core.Shared;

namespace Squish.Core.Optimizers;

/// <summary>Result of the sampled constant-quality search.</summary>
/// <param name="DuplicateFrameRatio">Fraction of sampled frames frame decimation would drop, when it was measured (GPU encodes only).</param>
internal sealed record VideoQualitySearchResult(int Crf, double Score, double PredictedBitrateKbps, bool TargetMet, double TargetScore, double? DuplicateFrameRatio = null);

internal interface IVideoQualitySearch
{
    /// <summary>
    /// Picks the CRF for <paramref name="plan"/>'s software encoder, or returns null when the search does not apply
    /// or could not run (the caller then keeps the fixed CRF).
    /// </summary>
    Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Same search, sharing work with the other searches of one job through <paramref name="cache"/>: the reference frames
    /// (and their prepared SSIMULACRA2 pyramids) are built once and reused when Auto drops a GPU plan for a software one.
    /// </summary>
    Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, VideoSearchCache? cache, CancellationToken cancellationToken)
        => SearchAsync(plan, context, cancellationToken);

    /// <summary>
    /// Fraction of frames frame decimation would drop, measured on a few short windows of <paramref name="source"/>, or null
    /// when it could not be measured. Decimation removes the frames of a static screen or slideshow before the encoder sees
    /// them, so it decides how much work an encode really is.
    /// </summary>
    Task<double?> MeasureDuplicateFrameRatioAsync(FilePath source, VideoProbeInfo probe, CancellationToken cancellationToken)
        => Task.FromResult<double?>(null);

    Task<double?> MeasureDuplicateFrameRatioAsync(FilePath source, VideoProbeInfo probe, VideoSearchCache? cache, CancellationToken cancellationToken)
        => MeasureDuplicateFrameRatioAsync(source, probe, cancellationToken);
}

/// <summary>
/// What the searches of one job can share: frame counts per sampled window (so the decimation measurement Auto takes before
/// choosing an encoder is not repeated when the search needs the same numbers) and the decoded, prepared reference frames
/// (so a GPU search that Auto abandons for software does not decode and prepare the same windows again). Dispose it when the
/// job ends.
/// </summary>
internal sealed class VideoSearchCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Kept, int Total)> _counts = new(StringComparer.Ordinal);
    private string? _referenceKey;
    private List<FfmpegVideoQualitySearch.SampleReference>? _references;

    /// <summary>Pixels of prepared reference tiles still allowed for this job (~36 bytes per pixel).</summary>
    internal long RemainingPreparedPixels = PerceptualReference.MaxCachedFullCoveragePixels;

    internal bool TryGetCounts(string key, out (int Kept, int Total) counts)
    {
        lock (_gate)
        {
            return _counts.TryGetValue(key, out counts);
        }
    }

    internal void StoreCounts(string key, (int Kept, int Total) counts)
    {
        lock (_gate)
        {
            _counts[key] = counts;
        }
    }

    internal List<FfmpegVideoQualitySearch.SampleReference>? TryGetReferences(string key)
    {
        lock (_gate)
        {
            return string.Equals(_referenceKey, key, StringComparison.Ordinal) ? _references : null;
        }
    }

    internal void StoreReferences(string key, List<FfmpegVideoQualitySearch.SampleReference> references)
    {
        lock (_gate)
        {
            DisposeReferences();
            _referenceKey = key;
            _references = references;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisposeReferences();
            _referenceKey = null;
        }
    }

    private void DisposeReferences()
    {
        if (_references is null)
        {
            return;
        }

        foreach (var reference in _references)
        {
            foreach (var tiles in reference.Tiles)
            {
                tiles.Dispose();
            }
        }

        _references = null;
    }
}

internal sealed class NullVideoQualitySearch : IVideoQualitySearch
{
    public static NullVideoQualitySearch Instance { get; } = new();

    public Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken)
        => Task.FromResult<VideoQualitySearchResult?>(null);
}

/// <summary>
/// Encodes a few short samples of the real footage at candidate CRFs and scores them against the source with
/// SSIMULACRA2 (see <see cref="CrfSearch"/> for the search itself). Clean screen recordings end up at a much
/// higher CRF than grainy handheld footage - exactly what a fixed CRF cannot do - and files that cannot be
/// meaningfully shrunk are detected before spending minutes on a full encode.
/// </summary>
internal sealed class FfmpegVideoQualitySearch : IVideoQualitySearch
{
    private readonly VideoOptimiserOptions _options;

    /// <summary>What is left of one search's allowance for prepared references (searches may run concurrently).</summary>
    private sealed class CacheBudget
    {
        public long RemainingPixels = PerceptualReference.MaxCachedFullCoveragePixels;
    }

    public FfmpegVideoQualitySearch(VideoOptimiserOptions options)
    {
        _options = options;
    }

    public Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken)
        => SearchAsync(plan, context, null, cancellationToken);

    public async Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, VideoSearchCache? cache, CancellationToken cancellationToken)
    {
        if (!_options.EnableQualitySearch
            || (plan.Encoder.UseHardwareEncoder && !HardwareConstantQuality.TryGetSearchRange(plan.Encoder, out _))
            || plan.Mode != VideoOptimiserMode.Video
            || plan.SourceProbe?.DurationSeconds is not double duration
            || duration < _options.QualitySearchMinimumDurationSeconds
            || !SupportsSearch(plan.Encoder.Codec))
        {
            return null;
        }

        // Decoding and scoring sample frames holds full-resolution frames in memory; draw on the same process-wide allowance
        // as pictures, so a 4K search cannot run on top of a batch of large images.
        var frameSize = plan.SourceProbe?.Video;
        var framePixels = frameSize is { Width: > 0, Height: > 0 } ? (long)frameSize.Width.Value * frameSize.Height.Value : 2_000_000L;
        using var pixelLease = await PixelBudget.Shared.AcquireAsync(framePixels, cancellationToken).ConfigureAwait(false);

        var workDirectory = TempRuns.CreateDirectory();

        // The references depend on the source and the filter chain only, not on the encoder, so a job's second search
        // (Auto abandoning the GPU for software) reuses the first one's.
        var referenceKey = $"{plan.SourcePath.Value}|{string.Join(',', plan.Filters)}";
        var references = cache?.TryGetReferences(referenceKey);
        var ownsReferences = references is null && cache is null;
        var reused = references is not null;
        references ??= new List<SampleReference>();
        var cacheBudget = new CacheBudget();
        if (cache is not null)
        {
            cacheBudget.RemainingPixels = cache.RemainingPreparedPixels;
        }

        try
        {
            context.ReportProgress(6, "Sampling footage to choose quality");
            var referenceClock = Stopwatch.StartNew();
            if (!reused)
            {
                var built = new List<SampleReference>();
                try
                {
                    foreach (var sample in PlanSamples(duration))
                    {
                        // Every window goes through the same filters, so the frame geometry measured on the first one holds for the rest.
                        (int, int)? knownSize = built.Count > 0 ? (built[0].Width, built[0].Height) : null;
                        var reference = await BuildReferenceAsync(plan, sample, cacheBudget, knownSize, cache, cancellationToken).ConfigureAwait(false);
                        if (reference is null)
                        {
                            DisposeReferences(built);
                            return null;
                        }

                        built.Add(reference);
                    }
                }
                catch
                {
                    DisposeReferences(built);
                    throw;
                }

                references = built;
                if (cache is not null)
                {
                    cache.RemainingPreparedPixels = cacheBudget.RemainingPixels;
                    cache.StoreReferences(referenceKey, built);
                }
            }

            Log.Debug($"Quality search: reference frames for {references.Count} window(s) {(reused ? "reused" : $"took {referenceClock.ElapsedMilliseconds} ms")}");
            var target = plan.AggressiveQuality ? _options.QualityTargetScoreAggressive : _options.QualityTargetScore;
            // GPU encoders are searched over their own constant-quality quantiser instead of a CRF.
            int minCrf, maxCrf, startCrf;
            double slope;
            if (plan.Encoder.UseHardwareEncoder && HardwareConstantQuality.TryGetSearchRange(plan.Encoder, out var hardware))
            {
                (minCrf, maxCrf, slope, startCrf) = (hardware.Min, hardware.Max, hardware.Slope, hardware.Start);
            }
            else
            {
                (minCrf, maxCrf, slope) = Range(plan.Encoder.Codec);
                startCrf = plan.Encoder.Quality;
            }

            async Task<CrfProbe?> Probe(int crf, CancellationToken token)
            {
                context.ReportProgress(8, $"Testing quality at CRF {crf}");
                return await MeasureAsync(plan, references, crf, workDirectory, token).ConfigureAwait(false);
            }

            var outcome = await CrfSearch.FindAsync(startCrf, minCrf, maxCrf, target, slope, Probe, _options.QualitySearchMaxProbes, cancellationToken).ConfigureAwait(false);
            if (outcome is null)
            {
                return null;
            }

            // Never pay for grain: cap the encode at a fraction of what the source itself costs.
            var sourceKbps = SourceVideoBitrateKbps(plan.SourceProbe);
            if (sourceKbps is double source && source > 0)
            {
                var ratio = plan.AggressiveQuality ? _options.QualitySearchMaxBitrateRatioAggressive : _options.QualitySearchMaxBitrateRatio;
                // ...but not by more than the allowance below the target: an efficient source is better kept than crushed.
                // The measured bitrate comes from short fast-preset samples that each start on a keyframe, so it overstates what
                // the full encode will cost; compare it against a correspondingly raised cap or normal-mode footage is crushed
                // for a "grain" problem it does not have.
                var sampleCap = source * ratio * Math.Max(1.0, _options.QualitySearchSampleBitrateInflation);
                outcome = await CrfSearch.EnforceBitrateCapAsync(outcome, sampleCap, maxCrf, Probe, cancellationToken, minScore: target - _options.QualitySearchCapAllowance).ConfigureAwait(false);
            }

            Log.Info($"Quality search for {plan.SourcePath.Name}: CRF {outcome.Chosen.Crf} score {outcome.Chosen.Score:0.0} (target {target:0.0}, met={outcome.TargetMet}) after {outcome.Probes.Count} probes");
            return new VideoQualitySearchResult(outcome.Chosen.Crf, outcome.Chosen.Score, outcome.Chosen.BitrateKbps, outcome.TargetMet, target, plan.RequiresFiltersBeyondDecimation ? null : MeasureDuplicateRatio(references));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warning($"Video quality search skipped: {ex.Message}");
            return null;
        }
        finally
        {
            if (ownsReferences)
            {
                DisposeReferences(references);
            }

            TryDeleteDirectory(workDirectory);
        }
    }

    private static void DisposeReferences(IEnumerable<SampleReference> references)
    {
        foreach (var reference in references)
        {
            foreach (var tiles in reference.Tiles)
            {
                tiles.Dispose();
            }
        }
    }

    /// <summary>Frames dropped by decimation over frames in the sampled windows, or null when it was not measured.</summary>
    private static double? MeasureDuplicateRatio(IReadOnlyList<SampleReference> references)
    {
        if (references.Count == 0 || references.Any(reference => reference.SourceFrames is null || reference.KeptFrames is null))
        {
            return null;
        }

        var total = references.Sum(reference => reference.SourceFrames!.Value);
        var kept = references.Sum(reference => reference.KeptFrames!.Value);
        return total <= 0 ? null : Math.Clamp(1.0 - (double)kept / total, 0.0, 1.0);
    }

    public Task<double?> MeasureDuplicateFrameRatioAsync(FilePath source, VideoProbeInfo probe, CancellationToken cancellationToken)
        => MeasureDuplicateFrameRatioAsync(source, probe, null, cancellationToken);

    public async Task<double?> MeasureDuplicateFrameRatioAsync(FilePath source, VideoProbeInfo probe, VideoSearchCache? cache, CancellationToken cancellationToken)
    {
        if (!_options.EnableFrameDecimation
            || probe.DurationSeconds is not double duration
            || duration <= 0)
        {
            return null;
        }

        var decimation = new[]
        {
            $"mpdecimate=hi={_options.FrameDecimationHighThreshold.ToInvariantString()}:lo={_options.FrameDecimationLowThreshold.ToInvariantString()}:max={_options.FrameDecimationMaxDifference.ToInvariantString()}"
        };

        try
        {
            double kept = 0, total = 0;
            foreach (var window in PlanSamples(duration))
            {
                // Frames in the window are counted, not derived from the container's frame rate, so variable-frame-rate
                // recordings (screen captures that idle, phone video) are measured correctly.
                if (await CountWindowFramesAsync(decimation, source.Value, window, cache, cancellationToken).ConfigureAwait(false) is not { } counts)
                {
                    return null;
                }

                kept += counts.Kept;
                total += counts.Total;
            }

            return total <= 0 ? null : Math.Clamp(1.0 - kept / total, 0.0, 1.0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning($"Could not measure frame decimation: {ex.Message}");
            return null;
        }
    }

    private static double? SourceVideoBitrateKbps(VideoProbeInfo? probe)
    {
        if (probe?.Video?.Bitrate is long videoBits && videoBits > 0)
        {
            return videoBits / 1000.0;
        }

        if (probe?.ContainerBitrate is long containerBits && containerBits > 0)
        {
            // Container bitrate includes audio; subtract what the audio track (if any) is known to use.
            var audio = probe.Audio?.Bitrate ?? 0;
            return Math.Max(1, (containerBits - audio) / 1000.0);
        }

        return null;
    }

    private static bool SupportsSearch(VideoCodec codec) => codec is VideoCodec.H264 or VideoCodec.Hevc or VideoCodec.Av1 or VideoCodec.Vp9;

    // Typical SSIMULACRA2 loss per CRF step measured on real footage at 1080p, per codec.
    private static (int Min, int Max, double Slope) Range(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => (16, 34, -2.8),
        VideoCodec.Hevc => (18, 36, -2.6),
        VideoCodec.Av1 => (22, 55, -0.8),
        VideoCodec.Vp9 => (24, 48, -1.4),
        _ => (18, 40, -2.0)
    };

    private IReadOnlyList<SampleWindow> PlanSamples(double duration)
    {
        var length = Math.Min(_options.QualitySearchSampleSeconds, Math.Max(1.0, duration / 3));
        var count = duration switch
        {
            < 8 => 1,
            < 40 => Math.Min(2, _options.QualitySearchMaxSamples),
            _ => Math.Min(3, _options.QualitySearchMaxSamples)
        };

        var windows = new List<SampleWindow>(count);
        for (var i = 0; i < count; i++)
        {
            var centre = duration * (i + 0.5) / count;
            var start = Math.Clamp(centre - length / 2, 0, Math.Max(0, duration - length));
            windows.Add(new SampleWindow(start, Math.Min(length, duration - start)));
        }

        return windows;
    }

    // ---- reference frames -----------------------------------------------------------------------------------

    private async Task<SampleReference?> BuildReferenceAsync(VideoOptimiserPlan plan, SampleWindow window, CacheBudget budget, (int Width, int Height)? knownSize, VideoSearchCache? cache, CancellationToken token)
    {
        var fps = plan.SourceProbe?.Video?.FrameRate is double rate && rate > 1 ? Math.Min(rate, plan.CapFps ? plan.TargetFps : rate) : 30.0;
        var expectedFrames = Math.Max(4, (int)(window.Seconds * fps));

        // `select` counts frames *after* the plan's filters. Frame decimation (on by default) drops most of a static screen
        // recording, so picks derived from the source frame rate can lie past the last frame that exists, leaving no
        // reference at all and silently disabling the whole search. Count what the filters really leave.
        // One pass yields both the frames the filters leave and the frames the window has. The first sets where the sample
        // picks can land; with the second, the share decimation removes decides whether a GPU encode is worth keeping
        // zero-copy (see VideoOptimiser.ApplyZeroCopyGate) and what Auto weighs.
        (int Kept, int Total)? counts = null;
        if (plan.Filters.Any(f => f.StartsWith("mpdecimate", StringComparison.Ordinal)))
        {
            counts = await CountWindowFramesAsync(plan.Filters, plan.SourcePath.Value, window, cache, token).ConfigureAwait(false);
            if (counts is { Kept: > 0 } known)
            {
                expectedFrames = Math.Min(expectedFrames, known.Kept);
            }
        }

        var picks = Enumerable.Range(0, _options.QualitySearchFramesPerSample)
            .Select(i => (int)((i + 0.5) * expectedFrames / _options.QualitySearchFramesPerSample))
            .Distinct()
            .ToArray();

        // Each full-resolution frame is reduced to its sample tiles as it arrives, so only one frame is alive at a time.
        int frameWidth = 0, frameHeight = 0;
        var references = await ReadFramesAsync(plan, plan.SourcePath.Value, window, picks, (_, frame) =>
        {
            frameWidth = frame.Width;
            frameHeight = frame.Height;
            var tiles = TileReference.Create(frame);

            // Keep the prepared reference of as many frames as the allowance covers (the same cap the image search uses), so
            // the second and later probes only pay for the candidate side of each comparison.
            if (tiles.TilePixels <= budget.RemainingPixels)
            {
                budget.RemainingPixels -= tiles.TilePixels;
                tiles.EnableCaching();
            }

            return tiles;
        }, token, knownSize: knownSize).ConfigureAwait(false);
        if (references.Count == 0)
        {
            return null;
        }

        return new SampleReference(window, picks, references, frameWidth, frameHeight)
        {
            KeptFrames = counts?.Kept,
            SourceFrames = counts?.Total
        };
    }

    /// <summary>
    /// Frames the window has once <paramref name="filters"/> have run (<c>Kept</c>) and frames the window has to begin with
    /// (<c>Total</c>), from one decode: the stream is split, one branch through the filters and one untouched, and ffmpeg
    /// reports how many frames each output received. Counting the second branch rather than reading "frames decoded" matters:
    /// seeking decodes from the previous keyframe, so that figure includes frames before the window. Null when ffmpeg
    /// could not tell.
    /// </summary>
    private async Task<(int Kept, int Total)?> CountWindowFramesAsync(IReadOnlyList<string> filters, string source, SampleWindow window, VideoSearchCache? cache, CancellationToken token)
    {
        var chain = filters.Count > 0 ? string.Join(',', filters) : "null";
        var key = string.Create(CultureInfo.InvariantCulture, $"{source}|{window.Start:0.###}|{window.Seconds:0.###}|{chain}");
        if (cache is not null && cache.TryGetCounts(key, out var cached))
        {
            return cached;
        }

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "verbose", "-nostdin", "-nostats",
            "-ss", window.Start.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", window.Seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", source,
            "-filter_complex", $"[0:v:0]split[a][b];[a]{chain}[k]",
            "-map", "[k]", "-an", "-sn", "-fps_mode", "passthrough", "-f", "null", "-",
            "-map", "[b]", "-an", "-sn", "-fps_mode", "passthrough", "-f", "null", "-"
        };

        var run = await ProcessRunner.RunAsync(_options.FfmpegPath, args, ProcessRunnerOptions.Create(throwOnError: false), token).ConfigureAwait(false);
        if (run.ExitCode != 0 || ParseOutputFrameCounts(run.StandardError) is not { } counts)
        {
            return null;
        }

        cache?.StoreCounts(key, counts);
        return counts;
    }

    /// <summary>
    /// Reads "Output stream #0:0 (video): N frames encoded" for the two outputs of the counting graph: output 0 is the
    /// filtered branch, output 1 the untouched one. Null when either line is missing or the window is empty.
    /// </summary>
    internal static (int Kept, int Total)? ParseOutputFrameCounts(string log)
    {
        int? kept = null, total = null;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(log, @"Output stream #(\d+):0 \(video\): (\d+) frames encoded"))
        {
            var frames = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (match.Groups[1].Value)
            {
                case "0":
                    kept = frames;
                    break;
                case "1":
                    total = frames;
                    break;
            }
        }

        return kept is int k && total is int t && t > 0 ? (k, t) : null;
    }

    /// <summary>
    /// Streams the selected frames from ffmpeg, handing each one to <paramref name="project"/> (frame position, frame) and
    /// keeping only what it returns. The frame must not be retained: a 4K RGBA frame is 33 MB on the Large Object Heap.
    /// </summary>
    private async Task<List<T>> ReadFramesAsync<T>(VideoOptimiserPlan plan, string input, SampleWindow window, int[] frameIndexes, Func<int, RasterImage, T> project, CancellationToken token, bool inputIsSample = false, (int Width, int Height)? knownSize = null)
    {
        var width = 0;
        var height = 0;

        // The reference path decodes the source through the exact filters the encode will use, so the comparison
        // measures codec loss only (scaling/fps changes are deliberate and are not "distortion").
        var filters = new List<string>();
        if (!inputIsSample && plan.Filters.Count > 0)
        {
            filters.AddRange(plan.Filters);
        }

        filters.Add("select='" + string.Join('+', frameIndexes.Select(i => $"eq(n\\,{i})")) + "'");

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (!inputIsSample)
        {
            args.AddRange(new[] { "-ss", window.Start.ToString("0.###", CultureInfo.InvariantCulture), "-t", window.Seconds.ToString("0.###", CultureInfo.InvariantCulture) });
        }

        args.AddRange(new[] { "-i", input, "-map", "0:v:0", "-an", "-sn", "-vf", string.Join(',', filters), "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" });

        // Frame geometry is unknown until ffmpeg has run the filters, so probe it from a one-frame dry run - unless the
        // caller already knows it (an encoded sample has exactly the geometry of the reference it is scored against).
        (width, height) = knownSize ?? await ProbeFrameSizeAsync(input, filters, window, inputIsSample, token).ConfigureAwait(false);
        if (width <= 0 || height <= 0)
        {
            return new List<T>();
        }

        var frameBytes = checked(width * height * 4);
        var frames = new List<T>();
        var startInfo = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var resolved = ToolLocator.ResolveOnPath(_options.FfmpegPath);
        if (resolved is null)
        {
            throw new FileNotFoundException($"Unable to locate '{_options.FfmpegPath}'.");
        }

        startInfo.FileName = resolved;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start ffmpeg.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        using var registration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        });

        var stream = process.StandardOutput.BaseStream;
        while (frames.Count < frameIndexes.Length)
        {
            // Frames are borrowed from the scratch pool: a 1080p frame is 8 MB and a 4K one 33 MB, so allocating one per
            // frame per probe would churn the Large Object Heap. `project` must not keep the frame (see the doc above).
            using var frame = RasterImage.Rent(width, height);
            var read = 0;
            while (read < frameBytes)
            {
                var n = await stream.ReadAsync(frame.Pixels.AsMemory(read, frameBytes - read), token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read < frameBytes)
            {
                break;
            }

            frames.Add(project(frames.Count, frame));
        }

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return frames;
    }

    private async Task<(int Width, int Height)> ProbeFrameSizeAsync(string input, IReadOnlyList<string> filters, SampleWindow window, bool inputIsSample, CancellationToken token)
    {
        // ffprobe on the container gives the pre-filter size; run a single filtered frame through ffmpeg to learn the real one.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (!inputIsSample)
        {
            args.AddRange(new[] { "-ss", window.Start.ToString("0.###", CultureInfo.InvariantCulture), "-t", window.Seconds.ToString("0.###", CultureInfo.InvariantCulture) });
        }

        var geometryFilters = filters.Where(f => !f.StartsWith("select=", StringComparison.Ordinal)).ToList();
        args.AddRange(new[] { "-i", input, "-map", "0:v:0", "-an", "-sn", "-frames:v", "1" });
        if (geometryFilters.Count > 0)
        {
            args.AddRange(new[] { "-vf", string.Join(',', geometryFilters) });
        }

        args.AddRange(new[] { "-f", "image2pipe", "-vcodec", "bmp", "pipe:1" });

        var startInfo = new ProcessStartInfo(ToolLocator.ResolveOnPath(_options.FfmpegPath) ?? throw new FileNotFoundException(_options.FfmpegPath))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start ffmpeg.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        var header = new byte[26];
        var read = 0;
        try
        {
            while (read < header.Length)
            {
                var n = await process.StandardOutput.BaseStream.ReadAsync(header.AsMemory(read), token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }
        finally
        {
            // Only the header is needed; always stop ffmpeg, including when the read is cancelled or throws.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // already exited
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // process is already terminating
            }
        }

        if (read < header.Length || header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            return (0, 0);
        }

        var width = BitConverter.ToInt32(header, 18);
        var height = Math.Abs(BitConverter.ToInt32(header, 22));
        return (width, height);
    }

    // ---- candidate measurement ----------------------------------------------------------------------------------

    private async Task<CrfProbe?> MeasureAsync(VideoOptimiserPlan plan, IReadOnlyList<SampleReference> references, int crf, string workDirectory, CancellationToken token)
    {
        var scores = new List<double>();
        double totalBytes = 0;
        double totalSeconds = 0;
        long encodeMs = 0, decodeMs = 0, scoreMs = 0;
        var clock = Stopwatch.StartNew();

        var encoder = VideoOptimiser.WithSearchedQuality(plan.Encoder, crf);
        var codecArgs = VideoOptimiser.BuildVideoCodecArguments(_options, encoder, plan.AggressiveQuality, plan.LookaheadFrames, plan.SourceProbe);
        // Samples are decoded in software, so the GPU's decode flags (input options) do not apply.
        codecArgs = FasterPresetForSampling(ExternalVideoToolchain.PartitionCodecArguments(codecArgs).EncoderScoped, encoder.Codec);

        for (var index = 0; index < references.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var reference = references[index];
            var samplePath = Path.Combine(workDirectory, $"sample-{index}-{crf}.mkv");

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", reference.Window.Start.ToString("0.###", CultureInfo.InvariantCulture), "-t", reference.Window.Seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", plan.SourcePath.Value };
            if (plan.Filters.Count > 0)
            {
                args.AddRange(new[] { "-vf", string.Join(',', plan.Filters) });
            }

            args.AddRange(new[] { "-map", "0:v:0", "-an", "-sn" });
            args.AddRange(codecArgs);
            args.AddRange(new[] { "-f", "matroska", samplePath });

            clock.Restart();
            var run = await ProcessRunner.RunAsync(_options.FfmpegPath, args, ProcessRunnerOptions.Create(throwOnError: false), token).ConfigureAwait(false);
            encodeMs += clock.ElapsedMilliseconds;
            if (run.ExitCode != 0 || !File.Exists(samplePath))
            {
                Log.Warning($"Sample encode failed (CRF {crf}): {run.StandardError}");
                return null;
            }

            totalBytes += new FileInfo(samplePath).Length;
            totalSeconds += reference.Window.Seconds;

            clock.Restart();
            long scoring = 0;
            var frameScores = await ReadFramesAsync(plan, samplePath, reference.Window, reference.FrameIndexes, (i, frame) =>
            {
                if (i >= reference.Tiles.Count || frame.Width != reference.Width || frame.Height != reference.Height)
                {
                    return (double?)null;
                }

                var scoreStart = Stopwatch.GetTimestamp();
                var value = reference.Tiles[i].Score(frame, token);
                scoring += (long)Stopwatch.GetElapsedTime(scoreStart).TotalMilliseconds;
                return value;
            }, token, inputIsSample: true, knownSize: (reference.Width, reference.Height)).ConfigureAwait(false);
            scoreMs += scoring;
            decodeMs += clock.ElapsedMilliseconds - scoring;
            if (frameScores.Count == 0)
            {
                return null;
            }

            foreach (var frameScore in frameScores)
            {
                if (frameScore is double value)
                {
                    scores.Add(value);
                }
            }

            TryDelete(samplePath);
        }

        if (scores.Count == 0 || totalSeconds <= 0)
        {
            return null;
        }

        // Judge by the weaker half of the frames: viewers notice the worst moments, not the average.
        Log.Debug($"Quality search probe {crf}: sample encodes {encodeMs} ms, decodes {decodeMs} ms, SSIMULACRA2 scoring {scoreMs} ms");
        scores.Sort();
        var worst = Math.Max(1, (scores.Count + 1) / 2);
        var score = scores.Take(worst).Average();
        return new CrfProbe(crf, score, totalBytes * 8 / totalSeconds / 1000.0);
    }

    /// <summary>Samples are encoded one speed notch faster than the final run: cheaper, and conservative (slower presets only improve quality).</summary>
    internal static IReadOnlyList<string> FasterPresetForSampling(IReadOnlyList<string> args, VideoCodec codec)
    {
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            // The hvc1 tag is for the final MP4/MOV; Matroska samples do not need it.
            if (token.Equals("-tag:v", StringComparison.Ordinal) && i + 1 < args.Count)
            {
                i++;
                continue;
            }

            result.Add(token);
            if (i + 1 >= args.Count)
            {
                continue;
            }

            if (token.Equals("-preset", StringComparison.Ordinal))
            {
                var value = args[++i];
                result.Add(SpeedUpPreset(value, codec));
            }
            else if (token.Equals("-quality", StringComparison.Ordinal))
            {
                // AMD AMF's speed knob: "quality" is its slowest preset, "balanced" about 1.6x faster.
                var value = args[++i];
                result.Add(value.Equals("quality", StringComparison.OrdinalIgnoreCase) ? "balanced" : value);
            }
            else if (token.Equals("-multipass", StringComparison.Ordinal))
            {
                // NVENC's two-pass analysis is much of what separates p7 from p4; samples skip it like the preset.
                i++;
                result.Add("disabled");
            }
            else if (token.Equals("-tune", StringComparison.Ordinal))
            {
                var value = args[++i];
                result.Add(value.Equals("uhq", StringComparison.OrdinalIgnoreCase) ? "hq" : value);
            }
            else if (token.Equals("-cpu-used", StringComparison.Ordinal))
            {
                var value = args[++i];
                result.Add(int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpuUsed) ? Math.Min(5, cpuUsed + 2).ToString(CultureInfo.InvariantCulture) : value);
            }
        }

        return result;
    }

    /// <summary>One speed notch faster for the codec's own preset vocabulary: SVT-AV1 numbers, x264/x265 and QSV names, NVENC p1-p7.</summary>
    private static string SpeedUpPreset(string value, VideoCodec codec)
    {
        // NVENC (any codec): p5-p9 -> p4, the encoder's "medium".
        if (value.Length == 2 && value[0] == 'p' && int.TryParse(value.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
        {
            return level > 4 ? "p4" : value;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return codec == VideoCodec.Av1 ? Math.Min(12, number + 2).ToString(CultureInfo.InvariantCulture) : value;
        }

        // Named presets (x264, x265, and Intel QSV for every codec including AV1).
        return SpeedUpX26x(value);
    }

    private static string SpeedUpX26x(string preset) => preset.ToLowerInvariant() switch
    {
        "veryslow" or "slower" or "slow" or "medium" => "fast",
        _ => preset
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    internal readonly record struct SampleWindow(double Start, double Seconds);

    internal sealed record SampleReference(SampleWindow Window, int[] FrameIndexes, IReadOnlyList<TileReference> Tiles, int Width, int Height)
    {
        /// <summary>Frames left after decimation, and frames before it; both set only when the duplicate ratio was measured.</summary>
        public int? KeptFrames { get; init; }

        public int? SourceFrames { get; init; }
    }
}
