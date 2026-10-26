using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Shared;
using ImageMagick;

namespace Squish.Core.Optimizers;

/// <summary>An output the job is willing to ship, plus the story of how it was made.</summary>
internal sealed record ImageCandidate(FilePath File, string Extension, string Description, long Size, bool Lossy, double? Score);

/// <summary>
/// One image optimisation run. The strategy is the same for every format: produce the safest candidate first
/// (lossless recompression), then try lossy encoders under a perceptual (SSIMULACRA2) guard, and ship whichever
/// valid candidate is smallest - or nothing when the original is already as good as it gets.
/// </summary>
internal sealed class ImageOptimisationJob : IDisposable
{
    // A lossy result must beat the lossless one by this much, otherwise quality is not worth trading for crumbs.
    private const double LossyAdvantageNormal = 0.94;
    private const double LossyAdvantageAggressive = 0.985;
    private const int SearchIterations = 7;
    private const int VerificationRetries = 3;

    private readonly OptimisationRequest _request;
    private readonly ImageOptimiserOptions _options;
    private readonly OptimiserExecutionContext _context;
    private readonly CancellationToken _token;
    private readonly TempFileScope _temp = new();
    private readonly ImageEncoders _encoders;
    private readonly bool _aggressive;
    private readonly double _targetScore;
    private readonly FilePath _source;
    private readonly long _originalSize;
    private readonly List<string> _notes = new();

    public ImageOptimisationJob(OptimisationRequest request, ImageOptimiserOptions options, OptimiserExecutionContext context, CancellationToken token)
    {
        _request = request;
        _options = options;
        _context = context;
        _token = token;
        _source = request.SourcePath;
        _originalSize = SafeSize(_source);
        _aggressive = OptimisationMetadata.GetFlag(request.Metadata, OptimisationMetadata.ImageAggressive);
        _targetScore = _aggressive ? options.PerceptualGuard.AggressiveMinimumScore : options.PerceptualGuard.MinimumScore;
        _encoders = new ImageEncoders(ImageToolset.Resolve(options.AdvancedCodecs), _temp);
    }

    public void Dispose() => _temp.Dispose();

    private bool GuardEnabled => _options.PerceptualGuard.Enabled;

    private int FixedQuality => _aggressive ? Math.Min(_options.TargetJpegQuality, 68) : _options.TargetJpegQuality;

    public async Task<OptimisationResult> RunAsync()
    {
        var started = DateTime.UtcNow;
        _context.ReportProgress(5, "Analysing image");

        var probe = ImageIO.Probe(_source.Value);
        if (probe is null)
        {
            return OptimisationResult.Failure(_request.RequestId, "Unsupported or unreadable image file.");
        }

        var limit = ValidateLimits(probe);
        if (limit is not null)
        {
            return OptimisationResult.Failure(_request.RequestId, limit);
        }

        ImageCandidate? best = probe.Format switch
        {
            SourceFormat.Jpeg => await OptimiseJpegAsync(probe).ConfigureAwait(false),
            SourceFormat.Png => await OptimisePngAsync(probe).ConfigureAwait(false),
            SourceFormat.Gif => await OptimiseGifAsync(probe).ConfigureAwait(false),
            _ => await OptimiseConvertibleAsync(probe).ConfigureAwait(false)
        };

        return Finish(best, started);
    }

    // ------------------------------------------------------------------------------------------------------
    // Result handling
    // ------------------------------------------------------------------------------------------------------

    private OptimisationResult Finish(ImageCandidate? best, DateTime started)
    {
        var elapsed = DateTime.UtcNow - started;

        if (best is null)
        {
            var reason = _notes.Count > 0 ? _notes[^1] : "Original already optimal";
            _context.ReportProgress(100, reason);
            return new OptimisationResult(_request.RequestId, OptimisationStatus.Succeeded, _source, reason, elapsed);
        }

        var convertedFormat = !string.Equals(best.Extension, _source.Extension, StringComparison.OrdinalIgnoreCase)
            && !(IsJpegExtension(best.Extension) && IsJpegExtension(_source.Extension));

        if (_options.RequireSizeImprovement)
        {
            // A format conversion has to actually shrink the file: WebP and AVIF are usually already tighter than the
            // JPEG or PNG they would become.
            var minimumSaving = convertedFormat ? 0 : _originalSize * (_options.MinimumSavingsPercent / 100.0);
            if (best.Size >= _originalSize - minimumSaving)
            {
                var kept = convertedFormat ? "Already compact; converting would not save space" : "Original already optimal";
                _context.ReportProgress(95, "No size improvement; keeping original");
                return new OptimisationResult(_request.RequestId, OptimisationStatus.Succeeded, _source, kept, elapsed);
            }
        }

        var plan = OptimisedOutputPlanner.Plan(_source, best.Extension, _request.Metadata, BuildOutputPath);
        var destination = plan.Destination;
        destination.EnsureParentDirectoryExists();
        File.Copy(best.File.Value, destination.Value, overwrite: true);

        if (plan.RequiresSourceDeletion(_source))
        {
            TryDelete(_source);
        }

        var message = DescribeImprovement(best);
        _context.ReportProgress(100, message);
        return new OptimisationResult(_request.RequestId, OptimisationStatus.Succeeded, destination, message, elapsed);
    }

    private string DescribeImprovement(ImageCandidate best)
    {
        var message = best.Description;
        if (_originalSize > 0 && best.Size > 0)
        {
            var diff = _originalSize - best.Size;
            message = diff > 0
                ? $"{best.Description}. Saved {diff.HumanSize()} ({_originalSize.HumanSize()} → {best.Size.HumanSize()})"
                : $"{best.Description}. Re-encoded";
        }

        if (best.Score is double score && _options.PerceptualGuard.RecordMetrics)
        {
            message += $" (quality score {score:0.0})";
        }

        return message;
    }

    private string? ValidateLimits(ImageProbe probe)
    {
        if (_options.MaxInputPixels is { } maxPixels && probe.PixelCount > maxPixels)
        {
            return $"Image exceeds pixel limit: {probe.Width}×{probe.Height} ({probe.PixelCount:N0} pixels) > allowed {maxPixels:N0}.";
        }

        if (_options.MaxDimension is { } maxDimension && (probe.Width > maxDimension || probe.Height > maxDimension))
        {
            return $"Image exceeds dimension limit: {probe.Width}×{probe.Height} > {maxDimension} on at least one edge.";
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------------
    // JPEG
    // ------------------------------------------------------------------------------------------------------

    private async Task<ImageCandidate?> OptimiseJpegAsync(ImageProbe probe)
    {
        var bytes = await File.ReadAllBytesAsync(_source.Value, _token).ConfigureAwait(false);
        var blobs = JpegSegments.Extract(bytes);
        var orientation = blobs.Exif is { Length: > 0 } exif ? JpegSegments.ReadOrientation(exif) : 1;
        var downscale = NeedsDownscale(probe);

        // jpegtran runs in its own process, so the lossless pass overlaps the lossy search.
        var losslessTask = downscale
            ? Task.FromResult<ImageCandidate?>(null)
            : LosslessJpegAsync(bytes, blobs, orientation);

        _context.ReportProgress(30, "Searching lowest visually-lossless quality");
        ImageCandidate? lossy = null;
        try
        {
            if (JpegSegments.HasSquishMarker(bytes))
            {
                // Already squeezed once: only the lossless pass may touch it, otherwise repeated runs keep eroding quality.
                _notes.Add("Already optimised by Squish; lossless pass only");
            }
            else
            {
                lossy = await LossyJpegFromSourceAsync(probe, blobs, downscale).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is MagickException or NotSupportedException or InvalidDataException)
        {
            _notes.Add($"Lossy pass skipped: {ex.Message}");
        }
        finally
        {
            // Observe the lossless pass on every path, including cancellation.
            await losslessTask.ConfigureAwait(false);
        }

        return PickBest(await losslessTask.ConfigureAwait(false), lossy);
    }

    private async Task<ImageCandidate?> LosslessJpegAsync(byte[] sourceBytes, MetadataBlobs blobs, int orientation)
    {
        var recompressed = await _encoders.RecompressJpegLosslessAsync(_source, _token).ConfigureAwait(false);
        if (recompressed is null)
        {
            return null;
        }

        var resolved = MetadataPlan.Resolve(blobs, _options.MetadataPolicy, orientationAlreadyApplied: false);
        if (resolved.Exif is null && orientation != 1)
        {
            // Pixels were not rotated, so the file must keep saying how to display them.
            resolved = resolved with { Exif = JpegSegments.BuildOrientationOnlyExif(orientation) };
        }

        var data = JpegSegments.Rewrite(await File.ReadAllBytesAsync(recompressed.Value.Value, _token).ConfigureAwait(false), resolved, addSquishMarker: JpegSegments.HasSquishMarker(sourceBytes));
        await File.WriteAllBytesAsync(recompressed.Value.Value, data, _token).ConfigureAwait(false);
        return new ImageCandidate(recompressed.Value, "jpg", "Lossless JPEG recompression", data.LongLength, false, null);
    }

    private async Task<ImageCandidate?> LossyJpegFromSourceAsync(ImageProbe probe, MetadataBlobs blobs, bool downscale)
    {
        using var decoded = ImageIO.Decode(_source.Value, SourceFormat.Jpeg, autoOrient: true);
        if (decoded.ColorKind is ImageColorKind.Cmyk or ImageColorKind.Other)
        {
            _notes.Add("CMYK JPEG left untouched to protect colours");
            return null;
        }

        var reference = ImageIO.ToRaster(PrepareImage(decoded.Image, downscale));
        if (!PerceptualReference.IsSupported(reference))
        {
            return null;
        }

        var content = ImageContentAnalyzer.Analyse(reference, hasAlpha: false);
        var sourceQuality = decoded.EstimatedJpegQuality;

        // Re-encoding above the source's own quality only adds bytes; re-encoding below needs the guard's blessing.
        var highest = _options.MaximumLossyQuality;
        if (sourceQuality > 0 && !downscale)
        {
            highest = Math.Min(highest, sourceQuality);
        }

        var lowest = Math.Min(_options.MinimumLossyQuality, highest);
        if (GuardEnabled && highest <= lowest + 2 && !downscale)
        {
            _notes.Add("JPEG is already heavily compressed; lossless pass only");
            return null;
        }

        var resolved = MetadataPlan.Resolve(blobs, _options.MetadataPolicy, orientationAlreadyApplied: true);
        var chromas = JpegChromaCandidates(content, decoded.JpegChroma420);

        var encoded = await EncodeJpegWithGuardAsync(reference, chromas, lowest, highest).ConfigureAwait(false);
        if (encoded is null)
        {
            return null;
        }

        return await WithJpegMetadataAsync(encoded, resolved, downscale ? "Downscaled and re-encoded JPEG" : "Perceptual JPEG re-encode").ConfigureAwait(false);
    }

    /// <summary>
    /// Chroma layouts worth searching. Photos usually win with 4:2:0, but re-subsampling a source that kept full
    /// chroma (4:4:4 exports, 4:2:2 cameras, PNG/HEIC conversions) often cannot reach the target at any quality,
    /// while 4:4:4 can - and sometimes is simply smaller. A 4:2:0 source has no chroma detail left to keep.
    /// </summary>
    private static ChromaSubsampling[] JpegChromaCandidates(ImageContentProfile content, bool? sourceIs420)
    {
        if (content.Kind != ImageContentKind.Photograph)
        {
            return [ChromaSubsampling.Full444];
        }

        return sourceIs420 == true
            ? [ChromaSubsampling.Subsampled420]
            : [ChromaSubsampling.Subsampled420, ChromaSubsampling.Full444];
    }

    private async Task<EncodedCandidate?> EncodeJpegWithGuardAsync(RasterImage reference, IReadOnlyList<ChromaSubsampling> chromas, int lowest, int highest)
    {
        var mozjpeg = _encoders.HasMozJpeg;

        if (!GuardEnabled || !PerceptualReference.IsSupported(reference))
        {
            var quality = Math.Clamp(FixedQuality, lowest, Math.Max(lowest, highest));
            var file = await _encoders.EncodeJpegAsync(reference, quality, chromas[0], _token).ConfigureAwait(false);
            return file is null ? null : new EncodedCandidate(file.Value, quality, double.NaN);
        }

        // The searches are independent (separate encoder processes, read-only reference), so run them side by side.
        var perceptual = PerceptualReference.Create(reference);
        // Every chroma layout is tried with each JPEG encoder; the smallest file that passes wins.
        var entries = new List<(ChromaSubsampling Chroma, string Encoder, Task<EncodedCandidate?> Search)>();
        foreach (var chroma in chromas)
        {
            var layout = chroma;
            entries.Add((layout, mozjpeg ? "mozjpeg" : "built-in encoder", SearchLossyAsync(reference, perceptual, lowest, highest,
                (image, quality, token) => _encoders.EncodeJpegAsync(image, quality, layout, token))));
            if (_encoders.HasJpegli)
            {
                entries.Add((layout, "jpegli", SearchLossyAsync(reference, perceptual, lowest, highest,
                    (image, quality, token) => _encoders.EncodeJpegliAsync(image, quality, layout, token))));
            }
        }

        await Task.WhenAll(entries.Select(e => e.Search)).ConfigureAwait(false);

        EncodedCandidate? best = null;
        var bestChroma = chromas[0];
        for (var i = 0; i < entries.Count; i++)
        {
            var chroma = entries[i].Chroma;
            var candidate = entries[i].Search.Result is { } found ? found with { Encoder = entries[i].Encoder } : null;
            if (candidate is null)
            {
                continue;
            }

            if (best is null || SafeSize(candidate.File) < SafeSize(best.File))
            {
                if (best is not null)
                {
                    _temp.Release(best.File);
                }

                best = candidate;
                bestChroma = chroma;
            }
            else
            {
                _temp.Release(candidate.File);
            }
        }

        if (best is not null)
        {
            var layout = bestChroma == ChromaSubsampling.Full444 ? "4:4:4" : "4:2:0";
            _notes.Add($"{best.Encoder} q{best.Quality} {layout}");
        }
        else
        {
            _notes.Add("No JPEG quality met the perceptual target");
        }

        return best;
    }

    private async Task<ImageCandidate> WithJpegMetadataAsync(EncodedCandidate encoded, MetadataBlobs metadata, string description)
    {
        var data = await File.ReadAllBytesAsync(encoded.File.Value, _token).ConfigureAwait(false);
        data = JpegSegments.Rewrite(data, metadata, addSquishMarker: true);
        await File.WriteAllBytesAsync(encoded.File.Value, data, _token).ConfigureAwait(false);

        var detail = encoded.Encoder is { } encoder ? $"{description} ({encoder} q{encoded.Quality})" : $"{description} (q{encoded.Quality})";
        return new ImageCandidate(encoded.File, "jpg", detail, data.LongLength, true, double.IsNaN(encoded.Score) ? null : encoded.Score);
    }

    // ------------------------------------------------------------------------------------------------------
    // PNG
    // ------------------------------------------------------------------------------------------------------

    private async Task<ImageCandidate?> OptimisePngAsync(ImageProbe probe)
    {
        var info = PngInfo.TryRead(_source.Value);
        if (info is { IsAnimated: true })
        {
            _notes.Add("Animated PNG left untouched");
            return null;
        }

        var downscale = NeedsDownscale(probe);
        var strip = PngStripMode();
        var candidates = new List<ImageCandidate>();

        // oxipng runs in its own process, so the lossless pass overlaps the decode and the lossy searches below.
        var losslessTask = downscale
            ? Task.FromResult<ImageCandidate?>(null)
            : LosslessPngAsync(_source, probe.PixelCount, strip, "Lossless PNG recompression");
        try
        {
            await OptimisePngLossyAsync(probe, info, downscale, strip, candidates).ConfigureAwait(false);
        }
        finally
        {
            // Always observe the lossless pass, even when the lossy work failed or was cancelled.
            if (await losslessTask.ConfigureAwait(false) is { } lossless)
            {
                candidates.Insert(0, lossless);
            }
        }

        return PickBest(candidates);
    }

    private async Task OptimisePngLossyAsync(ImageProbe probe, PngInfo? info, bool downscale, string strip, List<ImageCandidate> candidates)
    {
        var extension = SourceFormat.Png.DefaultExtension();
        var wantsJpeg = _options.FormatsToConvertToJpeg.Contains(extension);
        // Screenshots and UI graphics usually fit a 256-colour palette at the perceptual target for a fraction of the
        // lossless size; the guard rejects it where banding or dithering would show. An already-indexed source was
        // quantised before (possibly by us), so only aggressive mode may squeeze its palette further.
        var wantsLossyPalette = _encoders.HasPngquant && info is not { BitDepth: 16 } && (_aggressive || info is not { IsIndexed: true });
        var wantsModern = _options.AdvancedCodecs.AllowWebpOutput || _options.AdvancedCodecs.AllowAvifOutput;

        if (downscale || wantsJpeg || wantsLossyPalette || wantsModern)
        {
            _context.ReportProgress(30, "Evaluating lossy options");
            using var decoded = ImageIO.Decode(_source.Value, SourceFormat.Png, autoOrient: true);
            var reference = ImageIO.ToRaster(PrepareImage(decoded.Image, downscale));
            var hasAlpha = reference.HasVisibleAlpha();
            var content = ImageContentAnalyzer.Analyse(reference, hasAlpha);
            var blobs = new MetadataBlobs(decoded.ExifProfile, decoded.IccProfile, decoded.XmpProfile, decoded.IptcProfile);

            if (downscale)
            {
                var resized = await LosslessPngFromRasterAsync(decoded, downscale, strip, "Downscaled PNG").ConfigureAwait(false);
                if (resized is not null)
                {
                    candidates.Add(resized);
                }
            }

            if (wantsJpeg && !hasAlpha && content.IsPhotographic && reference.PixelCount >= 1_000_000 && PerceptualReference.IsSupported(reference))
            {
                var jpeg = await ConvertToJpegAsync(reference, content, blobs, "Converted photographic PNG to JPEG").ConfigureAwait(false);
                if (jpeg is not null)
                {
                    candidates.Add(jpeg);
                }
            }

            if (wantsLossyPalette && PerceptualReference.IsSupported(reference) && !HasNonSrgbProfile(blobs))
            {
                var palette = await QuantisedPngAsync(reference, strip).ConfigureAwait(false);
                if (palette is not null)
                {
                    candidates.Add(palette);
                }
            }

            if (wantsModern && PerceptualReference.IsSupported(reference) && !HasNonSrgbProfile(blobs))
            {
                var modern = await ModernFormatAsync(reference, content, hasAlpha).ConfigureAwait(false);
                if (modern is not null)
                {
                    candidates.Add(modern);
                }
            }
        }
    }

    private async Task<ImageCandidate?> LosslessPngAsync(FilePath input, long pixels, string strip, string description)
    {
        _context.ReportProgress(15, "Recompressing PNG");
        // Higher presets add more filter/deflate trials: worth 1-3% on screenshots while they stay around a second.
        var level = pixels switch
        {
            <= 2_000_000 => 6,
            <= 8_000_000 => 4,
            _ => 1
        };

        // Zopfli squeezes a few more percent but is an order of magnitude slower: aggressive mode and small images only.
        var zopfli = _aggressive && pixels <= 1_000_000;
        var output = await _encoders.OptimisePngLosslessAsync(input, level, zopfli, strip, _token).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }

        var size = SafeSize(output.Value);
        var tool = _encoders.HasOxipng ? (zopfli ? "oxipng + Zopfli" : "oxipng") : "built-in deflate";
        return new ImageCandidate(output.Value, "png", $"{description} ({tool})", size, false, null);
    }

    private async Task<ImageCandidate?> LosslessPngFromRasterAsync(DecodedImage decoded, bool downscale, string strip, string description)
    {
        var staging = _temp.New("squish-pngstage", "png");
        WriteDecodedAsPng(decoded, staging, downscale);
        return await LosslessPngAsync(staging, (long)decoded.Image.Width * decoded.Image.Height, strip, description).ConfigureAwait(false);
    }

    private async Task<ImageCandidate?> QuantisedPngAsync(RasterImage reference, string strip)
    {
        _context.ReportProgress(45, "Trying 256-colour palette");
        // Palette error is global (dithering, banding), so every pixel is judged rather than a few sampled tiles.
        var perceptual = GuardEnabled ? PerceptualReference.CreateFullCoverage(reference) : null;
        var staging = _encoders.WritePngStaging(reference);

        FilePath? bestFile = null;
        double? bestScore = null;
        var bestQuality = 0;

        // Quantises at a pngquant quality cap; the file comes back only when it meets the target.
        async Task<(int Quality, FilePath? File, double? Score)> ProbeAsync(int quality)
        {
            var quantised = await _encoders.QuantisePngAsync(staging, 0, quality, speed: 3, _token).ConfigureAwait(false);
            if (quantised is null)
            {
                return (quality, null, null);
            }

            if (perceptual is null)
            {
                return (quality, quantised, null);
            }

            var score = await Task.Run(() => perceptual.Score(ImageIO.DecodeToRaster(quantised.Value.Value), _token), _token).ConfigureAwait(false);
            if (score < _targetScore)
            {
                _temp.Release(quantised.Value);
                return (quality, null, score);
            }

            return (quality, quantised, score);
        }

        // Runs a round of caps side by side (pngquant is single-threaded and mostly I/O) and keeps the lowest passing cap.
        async Task<bool[]> RoundAsync(int[] caps)
        {
            _token.ThrowIfCancellationRequested();
            var results = await Task.WhenAll(caps.Select(ProbeAsync)).ConfigureAwait(false);
            foreach (var result in results.Where(r => r.File is not null).OrderBy(r => r.Quality))
            {
                if (bestFile is null || result.Quality < bestQuality)
                {
                    if (bestFile is not null)
                    {
                        _temp.Release(bestFile.Value);
                    }

                    (bestQuality, bestFile, bestScore) = result;
                }
                else
                {
                    _temp.Release(result.File!.Value);
                }
            }

            return results.Select(r => r.File is not null).ToArray();
        }

        // pngquant's 0-100 cap is not perceptual, so it is searched with our metric: lower caps let it drop colours and
        // dithering for much smaller files. Round one pairs the uncapped palette - the best any palette gets, so a miss
        // there (photos, gradients) settles it - with a mid cap; round two refines inside the bracket that leaves.
        const int lowestCap = 30;
        const int midCap = 65;
        var first = await RoundAsync([100, midCap]).ConfigureAwait(false);
        if (first[0])
        {
            var (low, high) = first[1] ? (lowestCap, midCap) : (midCap, 100);
            var refine = Enumerable.Range(1, 4).Select(i => low + (high - low) * i / 5).Distinct().ToArray();
            await RoundAsync(refine).ConfigureAwait(false);
        }

        if (bestFile is null)
        {
            _notes.Add("Palette quantisation would be visible; skipped");
            return null;
        }

        var polished = await _encoders.OptimisePngLosslessAsync(bestFile.Value, 3, zopfli: false, strip, _token).ConfigureAwait(false);
        var final = polished ?? bestFile.Value;
        return new ImageCandidate(final, "png", $"Quantised PNG palette (pngquant q{bestQuality})", SafeSize(final), true, bestScore);
    }

    // ------------------------------------------------------------------------------------------------------
    // WebP / AVIF / HEIC / BMP / TIFF
    // ------------------------------------------------------------------------------------------------------

    private async Task<ImageCandidate?> OptimiseConvertibleAsync(ImageProbe probe)
    {
        if (probe.FrameCount > 1)
        {
            _notes.Add($"Animated or multi-page {probe.Format} left untouched");
            return null;
        }

        var downscale = NeedsDownscale(probe);
        using var decoded = ImageIO.Decode(_source.Value, probe.Format, autoOrient: true);
        if (decoded.ColorKind is ImageColorKind.Cmyk or ImageColorKind.Other)
        {
            _notes.Add("Unsupported colour space left untouched");
            return null;
        }

        var reference = ImageIO.ToRaster(PrepareImage(decoded.Image, downscale));
        var hasAlpha = reference.HasVisibleAlpha();
        var content = ImageContentAnalyzer.Analyse(reference, hasAlpha);
        var blobs = new MetadataBlobs(decoded.ExifProfile, decoded.IccProfile, decoded.XmpProfile, decoded.IptcProfile);
        var extension = _source.Extension ?? probe.Format.DefaultExtension();
        var candidates = new List<ImageCandidate>();

        var convertToJpeg = _options.FormatsToConvertToJpeg.Contains(extension)
            || (probe.Format == SourceFormat.Heic && !_options.FormatsToConvertToPng.Contains(extension));
        var keepModernFormat = probe.Format is SourceFormat.WebP or SourceFormat.Avif
            && !convertToJpeg && !_options.FormatsToConvertToPng.Contains(extension);

        if (keepModernFormat)
        {
            var inFormat = await ModernFormatAsync(reference, content, hasAlpha, probe.Format).ConfigureAwait(false);
            if (inFormat is not null)
            {
                candidates.Add(inFormat);
            }
        }
        else if (convertToJpeg && !hasAlpha && content.IsPhotographic && PerceptualReference.IsSupported(reference))
        {
            var jpeg = await ConvertToJpegAsync(reference, content, blobs, $"Converted {probe.Format} to JPEG").ConfigureAwait(false);
            if (jpeg is not null)
            {
                candidates.Add(jpeg);
            }
        }

        if (candidates.Count == 0 && !keepModernFormat)
        {
            var staging = _temp.New("squish-conv", "png");
            WriteDecodedAsPng(decoded, staging, downscale);
            var png = await LosslessPngAsync(staging, reference.PixelCount, PngStripMode(), $"Converted {probe.Format} to PNG").ConfigureAwait(false);
            if (png is not null)
            {
                candidates.Add(png);
            }
        }

        // WebP and AVIF already beat JPEG and PNG at the same quality, so a conversion rarely shrinks them. When none
        // does, re-encode in the original format instead (Finish keeps the original if that is not smaller either).
        if (probe.Format is SourceFormat.WebP or SourceFormat.Avif && !keepModernFormat && !candidates.Any(c => c.Size < _originalSize))
        {
            var inFormat = await ModernFormatAsync(reference, content, hasAlpha, probe.Format).ConfigureAwait(false);
            if (inFormat is not null)
            {
                candidates.Add(inFormat);
            }
        }

        // Converted files change format; Finish decides whether the result is small enough to ship.
        return PickBest(candidates);
    }

    private async Task<ImageCandidate?> ConvertToJpegAsync(RasterImage reference, ImageContentProfile content, MetadataBlobs blobs, string description)
    {
        var chromas = JpegChromaCandidates(content, sourceIs420: null);
        var encoded = await EncodeJpegWithGuardAsync(reference, chromas, _options.MinimumLossyQuality, _options.MaximumLossyQuality).ConfigureAwait(false);
        if (encoded is null)
        {
            return null;
        }

        var resolved = MetadataPlan.Resolve(blobs, _options.MetadataPolicy, orientationAlreadyApplied: true);
        return await WithJpegMetadataAsync(encoded, resolved, description).ConfigureAwait(false);
    }

    /// <summary>WebP / AVIF encode under the perceptual guard (lossless WebP for flat graphics).</summary>
    private async Task<ImageCandidate?> ModernFormatAsync(RasterImage reference, ImageContentProfile content, bool hasAlpha, SourceFormat? onlyFormat = null)
    {
        var allowWebp = onlyFormat is null ? _options.AdvancedCodecs.AllowWebpOutput : onlyFormat == SourceFormat.WebP;
        var allowAvif = onlyFormat is null ? _options.AdvancedCodecs.AllowAvifOutput : onlyFormat == SourceFormat.Avif;
        var chroma = content.Kind == ImageContentKind.Photograph ? ChromaSubsampling.Subsampled420 : ChromaSubsampling.Full444;
        ImageCandidate? best = null;

        if (allowWebp && _encoders.HasWebp)
        {
            ImageCandidate? webp = null;
            if (content.Kind != ImageContentKind.Photograph)
            {
                var staging = _encoders.WritePngStaging(reference);
                var lossless = await _encoders.EncodeWebpAsync(staging, 100, lossless: true, _token).ConfigureAwait(false);
                if (lossless is not null)
                {
                    webp = new ImageCandidate(lossless.Value, "webp", "Lossless WebP", SafeSize(lossless.Value), false, null);
                }
            }

            if (webp is null && GuardEnabled && PerceptualReference.IsSupported(reference))
            {
                using var stagingCache = new StagingCache(_encoders);
                var perceptual = PerceptualReference.Create(reference);
                var encoded = await SearchLossyAsync(reference, perceptual, _options.MinimumLossyQuality, _options.MaximumLossyQuality,
                    async (image, quality, token) => await _encoders.EncodeWebpAsync(stagingCache.Get(image), quality, lossless: false, token).ConfigureAwait(false)).ConfigureAwait(false);
                if (encoded is not null)
                {
                    webp = new ImageCandidate(encoded.File, "webp", $"WebP q{encoded.Quality}", SafeSize(encoded.File), true, encoded.Score);
                }
            }

            best = Smaller(best, webp);
        }

        if (allowAvif && _encoders.HasAvif && GuardEnabled && PerceptualReference.IsSupported(reference))
        {
            using var stagingCache = new StagingCache(_encoders);
            var perceptual = PerceptualReference.Create(reference);
            var encoded = await SearchLossyAsync(reference, perceptual, _options.MinimumLossyQuality, _options.MaximumLossyQuality,
                async (image, quality, token) => await _encoders.EncodeAvifAsync(stagingCache.Get(image), quality, chroma, speed: 6, token).ConfigureAwait(false),
                iterations: 5).ConfigureAwait(false);
            if (encoded is not null)
            {
                best = Smaller(best, new ImageCandidate(encoded.File, "avif", $"AVIF q{encoded.Quality}", SafeSize(encoded.File), true, encoded.Score));
            }
        }

        if (best is null && onlyFormat is not null)
        {
            _notes.Add($"{onlyFormat} re-encode would not be smaller at the target quality");
        }

        return best;
    }

    // ------------------------------------------------------------------------------------------------------
    // GIF
    // ------------------------------------------------------------------------------------------------------

    // gifsicle --lossy levels, mildest first. Each step trades LZW-friendly colour noise for size; the search keeps the
    // strongest one whose sampled frames still score above the perceptual target.
    private static readonly int[] GifLossyLevels = { 8, 14, 20, 28, 40, 60, 90, 140, 200 };

    private const int GifGuardWindow = 72;
    private const int GifGuardSamples = 8;

    private async Task<ImageCandidate?> OptimiseGifAsync(ImageProbe probe)
    {
        _context.ReportProgress(20, "Optimising GIF frames");
        var lossless = await _encoders.OptimiseGifAsync(_source, lossy: 0, _token).ConfigureAwait(false);
        ImageCandidate? best = lossless is null
            ? null
            : new ImageCandidate(lossless.Value, "gif", $"Optimised GIF ({(_encoders.HasGifsicle ? "gifsicle" : "built-in")})", SafeSize(lossless.Value), false, null);

        if (_encoders.HasGifsicle)
        {
            ImageCandidate? lossy = null;
            if (GuardEnabled)
            {
                lossy = await SearchGifLossyAsync(probe).ConfigureAwait(false);
            }
            else if (_aggressive)
            {
                var file = await _encoders.OptimiseGifAsync(_source, lossy: 80, _token).ConfigureAwait(false);
                if (file is not null)
                {
                    lossy = new ImageCandidate(file.Value, "gif", "Lossy GIF (gifsicle)", SafeSize(file.Value), true, null);
                }
            }

            if (lossy is not null && lossy.Size < (best?.Size ?? _originalSize))
            {
                best = lossy;
            }
        }

        return PickBest(best);
    }

    /// <summary>
    /// Finds the strongest gifsicle lossy level that keeps evenly spaced composited frames above the perceptual
    /// target. Quality loss from the lossy LZW pass is uniform across an animation, so the leading frames are
    /// representative and long GIFs stay cheap to judge.
    /// </summary>
    private async Task<ImageCandidate?> SearchGifLossyAsync(ImageProbe probe)
    {
        var references = await Task.Run(() =>
        {
            var frames = ImageIO.DecodeGifSamples(_source.Value, GifGuardWindow, GifGuardSamples);
            return frames.Count > 0 && frames.All(f => PerceptualReference.IsSupported(f.Frame))
                ? frames.Select(f => (f.StartTicks, Reference: PerceptualReference.Create(f.Frame))).ToList()
                : null;
        }, _token).ConfigureAwait(false);

        if (references is null)
        {
            return null;
        }

        ImageCandidate? best = null;
        var low = 0;
        var high = GifLossyLevels.Length - 1;
        while (low <= high)
        {
            _token.ThrowIfCancellationRequested();
            var mid = (low + high) / 2;
            var level = GifLossyLevels[mid];
            var file = await _encoders.OptimiseGifAsync(_source, level, _token).ConfigureAwait(false);
            var score = file is null ? double.NegativeInfinity : await Task.Run(() => ScoreGifSamples(references, file.Value), _token).ConfigureAwait(false);
            if (file is not null && score >= _targetScore)
            {
                if (best is not null)
                {
                    _temp.Release(best.File);
                }

                best = new ImageCandidate(file.Value, "gif", $"Lossy GIF (gifsicle --lossy={level})", SafeSize(file.Value), true, score);
                low = mid + 1;
            }
            else
            {
                if (file is not null)
                {
                    _temp.Release(file.Value);
                }

                high = mid - 1;
            }
        }

        return best;
    }

    /// <summary>Mean SSIMULACRA2 over the sampled frames, or -inf when any single frame falls well short of the target.</summary>
    private double ScoreGifSamples(IReadOnlyList<(long StartTicks, PerceptualReference Reference)> references, FilePath candidate)
    {
        try
        {
            var frames = ImageIO.DecodeGifSamples(candidate.Value, GifGuardWindow, GifGuardSamples, references.Select(r => r.StartTicks).ToList());
            if (frames.Count != references.Count)
            {
                return double.NegativeInfinity;
            }

            var total = 0d;
            for (var i = 0; i < frames.Count; i++)
            {
                var reference = references[i].Reference.Reference;
                if (frames[i].Frame.Width != reference.Width || frames[i].Frame.Height != reference.Height)
                {
                    return double.NegativeInfinity;
                }

                var score = references[i].Reference.Score(frames[i].Frame, _token);
                if (score < _targetScore - GifWorstFrameSlack)
                {
                    return double.NegativeInfinity;
                }

                total += score;
            }

            return total / frames.Count;
        }
        catch (Exception ex) when (ex is MagickException or InvalidDataException)
        {
            return double.NegativeInfinity;
        }
    }

    private const double GifWorstFrameSlack = 10;

    // ------------------------------------------------------------------------------------------------------
    // Shared search machinery
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Bisects quality on a small mosaic of representative tiles (or the whole picture when it is small), then
    /// encodes the real image once and verifies it, nudging quality up if the full encode scores below target.
    /// </summary>
    private async Task<EncodedCandidate?> SearchLossyAsync(
        RasterImage reference,
        PerceptualReference perceptual,
        int lowest,
        int highest,
        Func<RasterImage, int, CancellationToken, Task<FilePath?>> encode,
        int iterations = SearchIterations)
    {
        var useMosaic = !perceptual.IsWholeImage;
        var probeImage = useMosaic ? perceptual.BuildMosaic() : reference;

        // When the probe is the whole image, the lowest passing encode already is the final, verified candidate.
        EncodedCandidate? passing = null;

        async Task<double?> Evaluate(int quality, CancellationToken token)
        {
            var file = await encode(probeImage, quality, token).ConfigureAwait(false);
            if (file is null)
            {
                return null;
            }

            var keep = false;
            try
            {
                var decoded = ImageIO.DecodeToRaster(file.Value.Value);
                var score = useMosaic ? perceptual.ScoreMosaic(decoded, token) : perceptual.Score(decoded, token);
                if (!useMosaic && score >= _targetScore && (passing is null || quality < passing.Quality))
                {
                    if (passing is not null)
                    {
                        _temp.Release(passing.File);
                    }

                    passing = new EncodedCandidate(file.Value, quality, score);
                    keep = true;
                }

                return score;
            }
            finally
            {
                if (!keep)
                {
                    _temp.Release(file.Value);
                }
            }
        }

        // Probe scores are kept so a re-search against a corrected target (below) only pays for new qualities.
        var probed = new Dictionary<int, double?>();
        async Task<double?> EvaluateCached(int quality, CancellationToken token)
        {
            if (!probed.TryGetValue(quality, out var score))
            {
                score = await Evaluate(quality, token).ConfigureAwait(false);
                probed[quality] = score;
            }

            return score;
        }

        var found = await LossySearch.FindLowestPassingAsync(lowest, highest, _targetScore, EvaluateCached, iterations, _token).ConfigureAwait(false);
        if (found is null)
        {
            if (passing is not null)
            {
                _temp.Release(passing.File);
            }

            return null;
        }

        if (passing is not null && passing.Quality == found.Value)
        {
            return passing;
        }

        if (passing is not null)
        {
            _temp.Release(passing.File);
        }

        // The search judged a few sampled tiles; the shipped file is judged on every pixel. Sampled tiles miss
        // low-frequency error such as chroma re-subsampling of a 4:2:2 / 4:4:4 source (seen 5+ points too generous).
        var verifier = perceptual.IsWholeImage ? perceptual : PerceptualReference.CreateFullCoverage(reference);
        var quality = found.Value;
        for (var attempt = 0; attempt < VerificationRetries; attempt++)
        {
            _token.ThrowIfCancellationRequested();
            var file = await encode(reference, quality, _token).ConfigureAwait(false);
            if (file is null)
            {
                return null;
            }

            var decoded = ImageIO.DecodeToRaster(file.Value.Value);
            var score = verifier.Score(decoded, _token);
            if (score >= _targetScore)
            {
                return new EncodedCandidate(file.Value, quality, score);
            }

            _temp.Release(file.Value);
            if (quality >= highest)
            {
                break;
            }

            // The probe was too generous by (probe score - real score) at this quality. Search the cheap probe again
            // against a target raised by that bias instead of blindly stepping up full-size encodes.
            var bias = probed.TryGetValue(quality, out var probeScore) && probeScore is double p ? Math.Max(0, p - score) : 0;
            var corrected = await LossySearch.FindLowestPassingAsync(quality + 1, highest, _targetScore + bias + 0.5, EvaluateCached, iterations, _token).ConfigureAwait(false);
            quality = corrected ?? highest;
        }

        return null;
    }

    private ImageCandidate? PickBest(params ImageCandidate?[] candidates) => PickBest((IEnumerable<ImageCandidate?>)candidates);

    private ImageCandidate? PickBest(IEnumerable<ImageCandidate?> candidates)
    {
        ImageCandidate? lossless = null;
        ImageCandidate? lossy = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            if (candidate.Lossy)
            {
                lossy = Smaller(lossy, candidate);
            }
            else
            {
                lossless = Smaller(lossless, candidate);
            }
        }

        if (lossy is null)
        {
            return lossless;
        }

        if (lossless is null)
        {
            return lossy;
        }

        var advantage = _aggressive ? LossyAdvantageAggressive : LossyAdvantageNormal;
        return lossy.Size < lossless.Size * advantage ? lossy : lossless;
    }

    private static ImageCandidate? Smaller(ImageCandidate? a, ImageCandidate? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return b.Size < a.Size ? b : a;
    }

    // ------------------------------------------------------------------------------------------------------
    // Pixel helpers
    // ------------------------------------------------------------------------------------------------------

    private bool NeedsDownscale(ImageProbe probe)
        => _options.DownscaleRetina && _options.RetinaLongEdgePixels > 0 && Math.Max(probe.Width, probe.Height) > _options.RetinaLongEdgePixels;

    /// <summary>Applies the optional retina downscale (Lanczos). The returned image is the pipeline's reference.</summary>
    private MagickImage PrepareImage(MagickImage image, bool downscale)
    {
        if (!downscale)
        {
            return image;
        }

        var longest = Math.Max(image.Width, image.Height);
        var scale = _options.RetinaLongEdgePixels / (double)longest;
        var width = (uint)Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = (uint)Math.Max(1, (int)Math.Round(image.Height * scale));
        _context.ReportProgress(25, $"Downscaling to {width}×{height}");
        image.FilterType = FilterType.Lanczos;
        image.Resize(width, height);
        return image;
    }

    private void WriteDecodedAsPng(DecodedImage decoded, FilePath destination, bool downscale)
    {
        using var clone = (MagickImage)decoded.Image.Clone();
        PrepareImage(clone, downscale);

        var keepIcc = _options.MetadataPolicy.PreserveColorProfiles && decoded.IccProfile is { } icc && !MetadataPlan.IsSrgbEquivalent(icc);
        clone.Strip();
        if (keepIcc)
        {
            clone.SetProfile(new ColorProfile(decoded.IccProfile!));
        }

        clone.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
        clone.Format = MagickFormat.Png;
        clone.Write(destination.Value);
    }

    private string PngStripMode()
    {
        var policy = _options.MetadataPolicy;
        if (policy.PreserveMetadata)
        {
            return policy.StripGpsMetadata ? "safe" : "none";
        }

        return policy.PreserveColorProfiles ? "safe" : "all";
    }

    private static bool HasNonSrgbProfile(MetadataBlobs blobs)
        => blobs.Icc is { Length: > 0 } icc && !MetadataPlan.IsSrgbEquivalent(icc);

    private static bool IsJpegExtension(string? extension)
        => string.Equals(extension, "jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, "jpeg", StringComparison.OrdinalIgnoreCase);

    private static FilePath BuildOutputPath(FilePath sourcePath, string extension)
    {
        var stem = sourcePath.Stem;
        if (stem.EndsWith(".squish", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^5];
        }

        return sourcePath.Parent.Append($"{stem}.squish.{extension}");
    }

    private static long SafeSize(FilePath path)
    {
        try
        {
            return File.Exists(path.Value) ? new FileInfo(path.Value).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryDelete(FilePath path)
    {
        try
        {
            if (File.Exists(path.Value))
            {
                File.Delete(path.Value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore cleanup issues
        }
    }

    /// <summary>Writes each distinct raster to a staging PNG once, however many qualities are tried.</summary>
    private sealed class StagingCache : IDisposable
    {
        private readonly ImageEncoders _encoders;
        private RasterImage? _image;
        private FilePath _file;

        public StagingCache(ImageEncoders encoders) => _encoders = encoders;

        public FilePath Get(RasterImage image)
        {
            if (!ReferenceEquals(image, _image))
            {
                _file = _encoders.WritePngStaging(image);
                _image = image;
            }

            return _file;
        }

        public void Dispose()
        {
            // Staging files are owned by the job's TempFileScope.
        }
    }
}
