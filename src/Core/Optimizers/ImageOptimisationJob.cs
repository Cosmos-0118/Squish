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

    /// <summary>"Always re-encode images": ignore the already-optimised shortcuts (Squish marker, quality ceiling, indexed PNG).</summary>
    private readonly bool _forceFull;
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
        _forceFull = OptimisationMetadata.GetFlag(request.Metadata, OptimisationMetadata.ImageForceFullOptimisation);
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

        // Pictures inside a PDF / Office file already hold a lease on the shared budget (the container pass took it before
        // calling us); taking it again here could deadlock, so only standalone images draw on it.
        using var lease = OptimisationMetadata.GetFlag(_request.Metadata, OptimisationMetadata.PixelBudgetHeld)
            ? null
            : await PixelBudget.Shared.AcquireAsync(probe.PixelCount, _token).ConfigureAwait(false);

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

        // Read before the copy: replacing the source in place would otherwise overwrite the timestamps we want to keep.
        var originalTimes = _options.PreserveDates ? ReadTimestamps(_source) : null;
        File.Copy(best.File.Value, destination.Value, overwrite: true);
        if (originalTimes is { } times)
        {
            // Must happen before the caller writes the optimisation marker, which records the file's last-write time.
            RestoreTimestamps(destination, times);
        }

        if (plan.RequiresSourceDeletion(_source))
        {
            TryDelete(_source);
        }

        var message = DescribeImprovement(best);
        _context.ReportProgress(100, message);
        return new OptimisationResult(_request.RequestId, OptimisationStatus.Succeeded, destination, message, elapsed);
    }

    private static (DateTime Created, DateTime Modified)? ReadTimestamps(FilePath path)
    {
        try
        {
            return (File.GetCreationTimeUtc(path.Value), File.GetLastWriteTimeUtc(path.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RestoreTimestamps(FilePath path, (DateTime Created, DateTime Modified) times)
    {
        try
        {
            File.SetCreationTimeUtc(path.Value, times.Created);
            File.SetLastWriteTimeUtc(path.Value, times.Modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Timestamps are a nicety; a locked or read-only file must not fail an otherwise finished optimisation.
        }
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
        var hasSquishMarker = JpegSegments.HasSquishMarker(bytes);

        // jpegtran runs in its own process, so the lossless pass overlaps the lossy search. Only the marker flag is
        // handed over so the whole source file is not kept rooted for the duration of the pass.
        var losslessTask = downscale
            ? Task.FromResult<ImageCandidate?>(null)
            : LosslessJpegAsync(hasSquishMarker, blobs, orientation);

        _context.ReportProgress(30, "Searching lowest visually-lossless quality");
        ImageCandidate? lossy = null;
        try
        {
            if (hasSquishMarker && !_forceFull)
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

    private async Task<ImageCandidate?> LosslessJpegAsync(bool sourceHasSquishMarker, MetadataBlobs blobs, int orientation)
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

        var data = JpegSegments.Rewrite(await File.ReadAllBytesAsync(recompressed.Value.Value, _token).ConfigureAwait(false), resolved, addSquishMarker: sourceHasSquishMarker);
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

        // The decoded pixels are not needed again on this path, so convert in place instead of cloning them natively.
        using var reference = ImageIO.ToRasterInPlace(PrepareImage(decoded.Image, downscale), pooled: true);
        // The managed copy is all the search needs (the properties read below are plain values); keeping ImageMagick's
        // native pixel cache alive through the whole encode search would hold a second full copy of the picture.
        decoded.Dispose();
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
        if (GuardEnabled && highest <= lowest + 2 && !downscale && !_forceFull)
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

    private async Task<EncodedCandidate?> EncodeJpegWithGuardAsync(RasterImage reference, IReadOnlyList<ChromaSubsampling> chromas, int lowest, int highest, SharedFullCoverage? sharedCoverage = null)
    {
        var mozjpeg = _encoders.HasMozJpeg;

        if (!GuardEnabled || !PerceptualReference.IsSupported(reference))
        {
            var quality = Math.Clamp(FixedQuality, lowest, Math.Max(lowest, highest));
            var file = await _encoders.EncodeJpegAsync(reference, quality, chromas[0], _token).ConfigureAwait(false);
            return file is null ? null : new EncodedCandidate(file.Value, quality, double.NaN);
        }

        // The searches are independent (separate encoder processes, read-only reference), so run them side by side.
        using var perceptual = PerceptualReference.Create(reference);
        // The final full-frame check is identical for every search, so they share one reference (and its prepared
        // pyramid) and take turns on it: concurrent full-frame decodes + scoring were the biggest memory spike. A caller that
        // goes on to judge the same picture again (the PNG palette pass) hands in its own so that reference is built only once.
        Verification? verification = null;
        var ownsVerification = false;
        if (!perceptual.IsWholeImage)
        {
            if (sharedCoverage is not null)
            {
                verification = sharedCoverage.Get();
            }
            else
            {
                verification = new Verification(PerceptualReference.CreateFullCoverage(reference), ownsReference: true);
                ownsVerification = true;
            }
        }

        // Every chroma layout is tried with each JPEG encoder; the smallest file that passes wins.
        var entries = new List<(ChromaSubsampling Chroma, string Encoder, Task<EncodedCandidate?> Search)>();
        foreach (var chroma in chromas)
        {
            var layout = chroma;
            entries.Add((layout, mozjpeg ? "mozjpeg" : "built-in encoder", SearchLossyAsync(reference, perceptual, lowest, highest,
                (image, quality, token) => _encoders.EncodeJpegAsync(image, quality, layout, token), verification: verification)));
            if (_encoders.HasJpegli)
            {
                entries.Add((layout, "jpegli", SearchLossyAsync(reference, perceptual, lowest, highest,
                    (image, quality, token) => _encoders.EncodeJpegliAsync(image, quality, layout, token), verification: verification)));
            }
        }

        try
        {
            // WhenAll completes only once every search has, so nothing is still scoring when the references go.
            await Task.WhenAll(entries.Select(e => e.Search)).ConfigureAwait(false);
        }
        finally
        {
            if (ownsVerification)
            {
                verification!.Dispose();
            }
        }

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
        var wantsLossyPalette = _encoders.HasPngquant && info is not { BitDepth: 16 }
            && (_aggressive || _forceFull || info is not { IsIndexed: true });
        var wantsModern = _options.AdvancedCodecs.AllowWebpOutput || _options.AdvancedCodecs.AllowAvifOutput;

        if (downscale || wantsJpeg || wantsLossyPalette || wantsModern)
        {
            _context.ReportProgress(30, "Evaluating lossy options");
            using var decoded = ImageIO.Decode(_source.Value, SourceFormat.Png, autoOrient: true);
            // The downscaled PNG candidate re-reads the decoded image later; otherwise it is free to consume.
            var prepared = PrepareImage(decoded.Image, downscale);
            using var reference = downscale ? ImageIO.ToRaster(prepared, pooled: true) : ImageIO.ToRasterInPlace(prepared, pooled: true);
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

            // Everything below works on the managed raster and the metadata copied above; release the native pixel cache now.
            decoded.Dispose();

            // The JPEG conversion and the palette search both judge the whole frame of this same picture; they share one
            // full-coverage reference (built on first use) instead of each constructing its own. Released before WebP/AVIF.
            using var coverage = new SharedFullCoverage(reference);
            var jpegProduced = false;
            if (wantsJpeg && !hasAlpha && content.IsPhotographic && reference.PixelCount >= _options.JpegConversionMinimumPixels && PerceptualReference.IsSupported(reference))
            {
                var jpeg = await ConvertToJpegAsync(reference, content, blobs, "Converted photographic PNG to JPEG", coverage).ConfigureAwait(false);
                if (jpeg is not null)
                {
                    candidates.Add(jpeg);
                    jpegProduced = true;
                }
            }

            // A large continuous-tone photo cannot hold 256 colours at the perceptual target; skip the two pngquant runs
            // and full-frame scoring that would only confirm it (aggressive/forced runs still try - unless a JPEG already
            // passed, which a 256-colour photo will not beat and which would otherwise score the whole frame a second time).
            var photoTooRich = content.IsPhotographic && reference.PixelCount >= 1_000_000 && ((!_aggressive && !_forceFull) || jpegProduced);
            // pngquant writes a fresh PNG without EXIF/XMP/IPTC, so it is only off when the user wants metadata kept AND
            // this file has some. A screenshot with nothing to lose can still be quantised.
            var wouldLoseMetadata = _options.MetadataPolicy.PreserveMetadata
                && (blobs.Exif is { Length: > 0 } || blobs.Xmp is { Length: > 0 } || blobs.Iptc is { Length: > 0 });
            if (wantsLossyPalette && !photoTooRich && !wouldLoseMetadata && PerceptualReference.IsSupported(reference) && !HasNonSrgbProfile(blobs))
            {
                var palette = await QuantisedPngAsync(reference, strip, coverage).ConfigureAwait(false);
                if (palette is not null)
                {
                    candidates.Add(palette);
                }
            }

            coverage.Dispose();

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

    private async Task<ImageCandidate?> QuantisedPngAsync(RasterImage reference, string strip, SharedFullCoverage? sharedCoverage = null)
    {
        _context.ReportProgress(45, "Trying 256-colour palette");
        // Palette error is global (dithering, banding), so every pixel is judged rather than a few sampled tiles.
        using var ownedScoring = GuardEnabled && sharedCoverage is null
            ? new Verification(PerceptualReference.CreateFullCoverage(reference), ownsReference: true)
            : null;
        var scoring = GuardEnabled ? ownedScoring ?? sharedCoverage!.Get() : null;
        var staging = _encoders.WritePngStaging(reference);

        FilePath? bestFile = null;
        double? bestScore = null;
        var bestQuality = 0;

        // pngquant runs side by side (cheap external processes), but decoding + scoring a candidate holds the whole
        // frame and a scoring workspace, so those take turns (the Verification's gate).

        // Quantises at a pngquant quality cap; the file comes back only when it meets the target.
        async Task<(int Quality, FilePath? File, double? Score)> ProbeAsync(int quality)
        {
            var quantised = await _encoders.QuantisePngAsync(staging, 0, quality, speed: 3, _token).ConfigureAwait(false);
            if (quantised is null)
            {
                return (quality, null, null);
            }

            if (scoring is null)
            {
                return (quality, quantised, null);
            }

            var score = await scoring.ScoreAsync(quantised.Value.Value, _token).ConfigureAwait(false);
            if (score < _targetScore)
            {
                _temp.Release(quantised.Value);
                return (quality, null, score);
            }

            return (quality, quantised, score);
        }

        // Runs a round of caps side by side (pngquant is single-threaded and mostly I/O) and keeps the smallest passing
        // file. Size is not monotonic in the cap: a lower cap drops colours but can dither more, and dither noise
        // deflates worse (a UI screenshot came out 19 KB at q37 against 12 KB at q51).
        async Task<bool[]> RoundAsync(int[] caps)
        {
            _token.ThrowIfCancellationRequested();
            var results = await Task.WhenAll(caps.Select(ProbeAsync)).ConfigureAwait(false);
            foreach (var result in results.Where(r => r.File is not null).OrderBy(r => r.Quality))
            {
                if (bestFile is null || SafeSize(result.File!.Value) < SafeSize(bestFile.Value))
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

        // AVIF and HEIC can describe their colours with CICP code points instead of an ICC profile. Everything below
        // goes through 8-bit sRGB, so BT.2020, Display P3 or PQ/HLG files would silently change meaning; leave them.
        if (probe.Format is SourceFormat.Avif or SourceFormat.Heic
            && IsoBmffColour.TryRead(_source.Value) is { } cicp && IsoBmffColour.IsBeyondSrgb(cicp))
        {
            _notes.Add($"HDR or wide-gamut colour signalling (CICP {cicp.Primaries}/{cicp.Transfer}/{cicp.Matrix}); left untouched to protect colours");
            return null;
        }

        var downscale = NeedsDownscale(probe);
        using var decoded = ImageIO.Decode(_source.Value, probe.Format, autoOrient: true);
        if (decoded.ColorKind is ImageColorKind.Cmyk or ImageColorKind.Other)
        {
            _notes.Add("Unsupported colour space left untouched");
            return null;
        }

        // Convert in place (no native clone of the whole picture) and release the native pixel cache straight away: everything
        // below works on the managed raster and the metadata copied here. The rare PNG fallback decodes the source again.
        using var reference = ImageIO.ToRasterInPlace(PrepareImage(decoded.Image, downscale), pooled: true);
        var hasAlpha = reference.HasVisibleAlpha();
        var content = ImageContentAnalyzer.Analyse(reference, hasAlpha);
        var blobs = new MetadataBlobs(decoded.ExifProfile, decoded.IccProfile, decoded.XmpProfile, decoded.IptcProfile);
        decoded.Dispose();
        var extension = _source.Extension ?? probe.Format.DefaultExtension();
        var candidates = new List<ImageCandidate>();

        var convertToJpeg = _options.FormatsToConvertToJpeg.Contains(extension)
            || (probe.Format == SourceFormat.Heic && !_options.FormatsToConvertToPng.Contains(extension));
        var keepModernFormat = probe.Format is SourceFormat.WebP or SourceFormat.Avif
            && !convertToJpeg && !_options.FormatsToConvertToPng.Contains(extension);

        // cwebp/avifenc are run without the source's ICC profile, so re-encoding a wide-gamut (Display P3, Adobe RGB)
        // WebP/AVIF would silently turn its colours into untagged sRGB. Leave those files as they are.
        var wideGamut = HasNonSrgbProfile(blobs);
        if (keepModernFormat && wideGamut)
        {
            _notes.Add("Wide-gamut colour profile; left untouched to protect colours");
            return null;
        }

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
            using (var again = ImageIO.Decode(_source.Value, probe.Format, autoOrient: true))
            {
                WriteDecodedAsPng(again, staging, downscale);
            }

            var png = await LosslessPngAsync(staging, reference.PixelCount, PngStripMode(), $"Converted {probe.Format} to PNG").ConfigureAwait(false);
            if (png is not null)
            {
                candidates.Add(png);
            }
        }

        // WebP and AVIF already beat JPEG and PNG at the same quality, so a conversion rarely shrinks them. When none
        // does, re-encode in the original format instead (Finish keeps the original if that is not smaller either).
        if (probe.Format is SourceFormat.WebP or SourceFormat.Avif && !keepModernFormat && !wideGamut && !candidates.Any(c => c.Size < _originalSize))
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

    private async Task<ImageCandidate?> ConvertToJpegAsync(RasterImage reference, ImageContentProfile content, MetadataBlobs blobs, string description, SharedFullCoverage? sharedCoverage = null)
    {
        var chromas = JpegChromaCandidates(content, sourceIs420: null);
        var encoded = await EncodeJpegWithGuardAsync(reference, chromas, _options.MinimumLossyQuality, _options.MaximumLossyQuality, sharedCoverage).ConfigureAwait(false);
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
        ImageCandidate? webp = null;
        ImageCandidate? avif = null;
        // One cache for the whole method, so the lossless encode and a whole-image lossy search share a staging PNG.
        using var stagingCache = new StagingCache(_encoders);

        if (allowWebp && _encoders.HasWebp)
        {
            ImageCandidate? losslessWebp = null;
            if (content.Kind != ImageContentKind.Photograph)
            {
                var lossless = await _encoders.EncodeWebpAsync(stagingCache.Get(reference), 100, lossless: true, _token).ConfigureAwait(false);
                if (lossless is not null)
                {
                    losslessWebp = new ImageCandidate(lossless.Value, "webp", "Lossless WebP", SafeSize(lossless.Value), false, null);
                }
            }

            ImageCandidate? lossyWebp = null;
            if (GuardEnabled && PerceptualReference.IsSupported(reference))
            {
                using var perceptual = PerceptualReference.Create(reference);
                var encoded = await SearchLossyAsync(reference, perceptual, _options.MinimumLossyQuality, _options.MaximumLossyQuality,
                    async (image, quality, token) => await _encoders.EncodeWebpAsync(stagingCache.Get(image), quality, lossless: false, token).ConfigureAwait(false)).ConfigureAwait(false);
                if (encoded is not null)
                {
                    lossyWebp = new ImageCandidate(encoded.File, "webp", $"WebP q{encoded.Quality}", SafeSize(encoded.File), true, encoded.Score);
                }
            }

            // Lossless is the baseline for graphics; the guarded lossy encode has to beat it by the usual margin.
            webp = PickBest(losslessWebp, lossyWebp);
        }

        if (allowAvif && _encoders.HasAvif && GuardEnabled && PerceptualReference.IsSupported(reference))
        {
            using var perceptual = PerceptualReference.Create(reference);
            var encoded = await SearchLossyAsync(reference, perceptual, _options.MinimumLossyQuality, _options.MaximumLossyQuality,
                async (image, quality, token) => await _encoders.EncodeAvifAsync(stagingCache.Get(image), quality, chroma, speed: 6, token).ConfigureAwait(false),
                iterations: 5).ConfigureAwait(false);
            if (encoded is not null)
            {
                avif = new ImageCandidate(encoded.File, "avif", $"AVIF q{encoded.Quality}", SafeSize(encoded.File), true, encoded.Score);
            }
        }

        var best = PickBest(webp, avif);

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

        try
        {
            return await BisectGifLossyAsync(references).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (_, frameReference) in references)
            {
                frameReference.Dispose();
            }
        }
    }

    private async Task<ImageCandidate?> BisectGifLossyAsync(List<(long StartTicks, PerceptualReference Reference)> references)
    {
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
    /// A full-frame reference plus a gate so only one decode-and-score runs at a time per job: each holds the decoded
    /// frame and a whole scoring workspace, which is tens to hundreds of MB for large images.
    /// </summary>
    private sealed class Verification : IDisposable
    {
        private readonly PerceptualReference _reference;
        private readonly bool _ownsReference;
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <param name="ownsReference">Dispose <paramref name="reference"/> with this verification (false when others still use it).</param>
        public Verification(PerceptualReference reference, bool ownsReference)
        {
            _reference = reference;
            _ownsReference = ownsReference;
        }

        public void Dispose()
        {
            if (_ownsReference)
            {
                _reference.Dispose();
            }
        }

        public async Task<double> ScoreAsync(string path, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => DecodeAndScore(path, token), token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Runs a full-frame encode and its score as one turn. A full-frame encoder process holds the whole picture's
        /// coefficients, so letting every parallel search start one before queueing for the score meant up to four at once.
        /// </summary>
        public async Task<(FilePath? File, double Score)> EncodeAndScoreAsync(Func<Task<FilePath?>> encode, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var file = await encode().ConfigureAwait(false);
                if (file is null)
                {
                    return (null, 0);
                }

                var score = await Task.Run(() => DecodeAndScore(file.Value.Value, token), token).ConfigureAwait(false);
                return (file, score);
            }
            finally
            {
                _gate.Release();
            }
        }

        // The decoded candidate is thrown away after one score, so its pixels are borrowed from the scratch pool.
        private double DecodeAndScore(string path, CancellationToken token)
        {
            using var decoded = ImageIO.DecodeToRaster(path, pooled: true);
            return _reference.Score(decoded, token);
        }
    }

    /// <summary>
    /// A full-coverage <see cref="Verification"/> created the first time a pass needs it and released with the owner, so the
    /// JPEG conversion and the palette search of one PNG judge it against the same reference.
    /// </summary>
    private sealed class SharedFullCoverage(RasterImage reference) : IDisposable
    {
        private readonly object _gate = new();
        private Verification? _verification;

        public Verification Get()
        {
            lock (_gate)
            {
                return _verification ??= new Verification(PerceptualReference.CreateFullCoverage(reference), ownsReference: true);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _verification?.Dispose();
                _verification = null;
            }
        }
    }

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
        int iterations = SearchIterations,
        Verification? verification = null)
    {
        var useMosaic = !perceptual.IsWholeImage;
        // The mosaic belongs to the reference and is built once for all the searches that share it.
        var probeImage = useMosaic ? perceptual.Mosaic : reference;

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
                using var decoded = ImageIO.DecodeToRaster(file.Value.Value, pooled: true);
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
        Verification? ownedVerification = null;
        if (verification is null)
        {
            // A whole-image reference already is the full-frame one (and belongs to the caller, who may be sharing it).
            verification = perceptual.IsWholeImage
                ? new Verification(perceptual, ownsReference: false)
                : ownedVerification = new Verification(PerceptualReference.CreateFullCoverage(reference), ownsReference: true);
        }

        try
        {
            var quality = found.Value;
            for (var attempt = 0; attempt < VerificationRetries; attempt++)
            {
                _token.ThrowIfCancellationRequested();
                var (file, score) = await verification.EncodeAndScoreAsync(() => encode(reference, quality, _token), _token).ConfigureAwait(false);
                if (file is null)
                {
                    return null;
                }

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
        finally
        {
            ownedVerification?.Dispose();
        }
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
        const string suffix = ".squish";
        if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^suffix.Length];
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
