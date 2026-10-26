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
internal sealed record VideoQualitySearchResult(int Crf, double Score, double PredictedBitrateKbps, bool TargetMet, double TargetScore);

internal interface IVideoQualitySearch
{
    /// <summary>
    /// Picks the CRF for <paramref name="plan"/>'s software encoder, or returns null when the search does not apply
    /// or could not run (the caller then keeps the fixed CRF).
    /// </summary>
    Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken);
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

    public FfmpegVideoQualitySearch(VideoOptimiserOptions options)
    {
        _options = options;
    }

    public async Task<VideoQualitySearchResult?> SearchAsync(VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken)
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

        var workDirectory = Path.Combine(Path.GetTempPath(), "squish-vq-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDirectory);
            var samples = PlanSamples(duration);
            var references = new List<SampleReference>();

            context.ReportProgress(6, "Sampling footage to choose quality");
            foreach (var sample in samples)
            {
                var reference = await BuildReferenceAsync(plan, sample, cancellationToken).ConfigureAwait(false);
                if (reference is null)
                {
                    return null;
                }

                references.Add(reference);
            }

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
                outcome = await CrfSearch.EnforceBitrateCapAsync(outcome, source * ratio, maxCrf, Probe, cancellationToken, minScore: target - _options.QualitySearchCapAllowance).ConfigureAwait(false);
            }

            Log.Info($"Quality search for {plan.SourcePath.Name}: CRF {outcome.Chosen.Crf} score {outcome.Chosen.Score:0.0} (target {target:0.0}, met={outcome.TargetMet}) after {outcome.Probes.Count} probes");
            return new VideoQualitySearchResult(outcome.Chosen.Crf, outcome.Chosen.Score, outcome.Chosen.BitrateKbps, outcome.TargetMet, target);
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
            TryDeleteDirectory(workDirectory);
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

    private async Task<SampleReference?> BuildReferenceAsync(VideoOptimiserPlan plan, SampleWindow window, CancellationToken token)
    {
        var fps = plan.SourceProbe?.Video?.FrameRate is double rate && rate > 1 ? Math.Min(rate, plan.CapFps ? plan.TargetFps : rate) : 30.0;
        var expectedFrames = Math.Max(4, (int)(window.Seconds * fps));
        var picks = Enumerable.Range(0, _options.QualitySearchFramesPerSample)
            .Select(i => (int)((i + 0.5) * expectedFrames / _options.QualitySearchFramesPerSample))
            .Distinct()
            .ToArray();

        var frames = await ReadFramesAsync(plan, plan.SourcePath.Value, window, picks, token).ConfigureAwait(false);
        if (frames.Count == 0)
        {
            return null;
        }

        var references = frames.Select(f => TileReference.Create(f)).ToList();
        return new SampleReference(window, picks, references, frames[0].Width, frames[0].Height);
    }

    private async Task<List<RasterImage>> ReadFramesAsync(VideoOptimiserPlan plan, string input, SampleWindow window, int[] frameIndexes, CancellationToken token, bool inputIsSample = false)
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

        // Frame geometry is unknown until ffmpeg has run the filters, so probe it from a one-frame dry run.
        (width, height) = await ProbeFrameSizeAsync(input, filters, window, inputIsSample, token).ConfigureAwait(false);
        if (width <= 0 || height <= 0)
        {
            return new List<RasterImage>();
        }

        var frameBytes = checked(width * height * 4);
        var frames = new List<RasterImage>();
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
            var buffer = new byte[frameBytes];
            var read = 0;
            while (read < frameBytes)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, frameBytes - read), token).ConfigureAwait(false);
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

            frames.Add(new RasterImage(width, height, buffer));
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
        while (read < header.Length)
        {
            var n = await process.StandardOutput.BaseStream.ReadAsync(header.AsMemory(read), token).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

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

            var run = await ProcessRunner.RunAsync(_options.FfmpegPath, args, ProcessRunnerOptions.Create(throwOnError: false), token).ConfigureAwait(false);
            if (run.ExitCode != 0 || !File.Exists(samplePath))
            {
                Log.Warning($"Sample encode failed (CRF {crf}): {run.StandardError}");
                return null;
            }

            totalBytes += new FileInfo(samplePath).Length;
            totalSeconds += reference.Window.Seconds;

            var decoded = await ReadFramesAsync(plan, samplePath, reference.Window, reference.FrameIndexes, token, inputIsSample: true).ConfigureAwait(false);
            if (decoded.Count == 0)
            {
                return null;
            }

            for (var i = 0; i < Math.Min(decoded.Count, reference.Tiles.Count); i++)
            {
                if (decoded[i].Width == reference.Width && decoded[i].Height == reference.Height)
                {
                    scores.Add(reference.Tiles[i].Score(decoded[i], token));
                }
            }

            TryDelete(samplePath);
        }

        if (scores.Count == 0 || totalSeconds <= 0)
        {
            return null;
        }

        // Judge by the weaker half of the frames: viewers notice the worst moments, not the average.
        scores.Sort();
        var worst = Math.Max(1, (scores.Count + 1) / 2);
        var score = scores.Take(worst).Average();
        return new CrfProbe(crf, score, totalBytes * 8 / totalSeconds / 1000.0);
    }

    /// <summary>Samples are encoded one speed notch faster than the final run: cheaper, and conservative (slower presets only improve quality).</summary>
    private static IReadOnlyList<string> FasterPresetForSampling(IReadOnlyList<string> args, VideoCodec codec)
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
                result.Add(codec switch
                {
                    VideoCodec.Av1 when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preset) => Math.Min(12, preset + 2).ToString(CultureInfo.InvariantCulture),
                    VideoCodec.H264 or VideoCodec.Hevc => SpeedUpX26x(value),
                    _ => value
                });
            }
            else if (token.Equals("-cpu-used", StringComparison.Ordinal))
            {
                var value = args[++i];
                result.Add(int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpuUsed) ? Math.Min(5, cpuUsed + 2).ToString(CultureInfo.InvariantCulture) : value);
            }
        }

        return result;
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

    private readonly record struct SampleWindow(double Start, double Seconds);

    private sealed record SampleReference(SampleWindow Window, int[] FrameIndexes, IReadOnlyList<TileReference> Tiles, int Width, int Height);
}
