using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Settings;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

/// <summary>
/// Windows port of <c>Video.swift</c>: orchestrates ffmpeg re-encodes, fps caps, optional GIF export,
/// and progress reporting so higher layers can mirror the macOS UX.
/// </summary>
public sealed class VideoOptimiser : IOptimiser
{
    private readonly VideoOptimiserOptions _options;
    private readonly IVideoToolchain _toolchain;
    private readonly IVideoMetadataProbe _metadataProbe;

    public VideoOptimiser(VideoOptimiserOptions? options = null, IVideoToolchain? toolchain = null, IVideoMetadataProbe? metadataProbe = null)
    {
        _options = options ?? VideoOptimiserOptions.Default;
        _toolchain = toolchain ?? new ExternalVideoToolchain(_options);
        _metadataProbe = metadataProbe ?? new FfprobeMetadataProbe(_options);
    }

    public ItemType ItemType => ItemType.Video;

    public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var source = request.SourcePath;
        if (!File.Exists(source.Value))
        {
            return OptimisationResult.Failure(request.RequestId, $"Source file not found: {source.Value}");
        }

        if (!MediaFormats.IsVideo(source))
        {
            return OptimisationResult.Unsupported(request.RequestId);
        }

        var probe = await _metadataProbe.ProbeAsync(source, cancellationToken).ConfigureAwait(false);
        var plan = BuildPlan(request, _options, probe);
        return plan.Mode switch
        {
            VideoOptimiserMode.Gif when !_options.EnableGifExport
                => OptimisationResult.Failure(request.RequestId, "GIF export disabled in configuration."),
            VideoOptimiserMode.Gif => await RunAnimatedOptimisationAsync(request, plan, context, cancellationToken).ConfigureAwait(false),
            _ => await RunVideoOptimisationAsync(request, plan, context, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<OptimisationResult> RunVideoOptimisationAsync(OptimisationRequest request, VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        if (plan.ShouldRemux)
        {
            return await RunRemuxOptimisationAsync(request, plan, context, cancellationToken).ConfigureAwait(false);
        }

        context.ReportProgress(5, "Preparing encoder");
        var tempOutput = FilePath.TempFile("clop-video", plan.OutputExtension, addUniqueSuffix: true);
        tempOutput.EnsureParentDirectoryExists();

        var currentPlan = plan;
        var originalSize = SafeFileSize(plan.SourcePath);
        long optimisedSize = 0;
        var hardwareRetryCount = 0;

        try
        {
            while (true)
            {
                var toolchainResult = await _toolchain.TranscodeAsync(currentPlan, tempOutput, context, cancellationToken).ConfigureAwait(false);
                if (!toolchainResult.Success)
                {
                    return OptimisationResult.Failure(request.RequestId, toolchainResult.ErrorMessage ?? "Video optimisation failed");
                }

                if (!File.Exists(tempOutput.Value))
                {
                    return OptimisationResult.Failure(request.RequestId, "Video optimiser produced no output file");
                }

                optimisedSize = SafeFileSize(tempOutput);

                if (ShouldRetryHardwareSavings(_options, currentPlan, originalSize, optimisedSize, hardwareRetryCount))
                {
                    if (TryCreateTightenedHardwarePlan(currentPlan, _options) is { } tightenedPlan)
                    {
                        hardwareRetryCount++;
                        context.ReportProgress(8, "Reducing hardware bitrate");
                        TryDelete(tempOutput);
                        currentPlan = tightenedPlan;
                        continue;
                    }
                }

                plan = currentPlan;
                break;
            }

            var savingsPercent = CalculateSavingsPercent(originalSize, optimisedSize);
            var requiresFallback = plan.UseHardwareAcceleration
                && plan.SoftwareFallback is not null
                && plan.RequireSizeReduction
                && (optimisedSize >= originalSize
                    || (savingsPercent.HasValue && savingsPercent.Value < _options.HardwareMinimumSavingsPercent));

            if (requiresFallback)
            {
                context.ReportProgress(10, "Retrying with software encoder");
                TryDelete(tempOutput);

                var softwareFallback = plan.SoftwareFallback!;
                var fallbackArgs = BuildVideoCodecArguments(_options, softwareFallback, plan.AggressiveQuality, plan.LookaheadFrames);
                var fallbackPlan = plan with
                {
                    Encoder = softwareFallback,
                    SoftwareFallback = null,
                    UseHardwareAcceleration = softwareFallback.UseHardwareEncoder,
                    VideoCodecArguments = fallbackArgs
                };

                var fallbackResult = await _toolchain.TranscodeAsync(fallbackPlan, tempOutput, context, cancellationToken).ConfigureAwait(false);
                if (!fallbackResult.Success)
                {
                    return OptimisationResult.Failure(request.RequestId, fallbackResult.ErrorMessage ?? "Software fallback failed");
                }

                plan = fallbackPlan;
                optimisedSize = SafeFileSize(tempOutput);
                savingsPercent = CalculateSavingsPercent(originalSize, optimisedSize);
            }

            var outputPlan = OptimisedOutputPlanner.Plan(plan.SourcePath, plan.OutputExtension, request.Metadata, BuildCopyOutputPath);
            var finalOutput = outputPlan.Destination;
            finalOutput.EnsureParentDirectoryExists();

            var requireSizeCheck = plan.RequireSizeReduction && !plan.ForceMp4;
            if (requireSizeCheck && optimisedSize >= originalSize)
            {
                context.ReportProgress(100, "Original already optimal");
                return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, plan.SourcePath, "Original already optimal");
            }

            File.Copy(tempOutput.Value, finalOutput.Value, overwrite: true);
            CopyTimestamps(plan.SourcePath, finalOutput, plan.PreserveTimestamps);

            if (outputPlan.RequiresSourceDeletion(plan.SourcePath))
            {
                TryDelete(plan.SourcePath);
            }

            var message = DescribeImprovement(originalSize, optimisedSize, plan);
            context.ReportProgress(100, message);
            return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, finalOutput, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Video optimisation failed: {ex.Message}");
            return OptimisationResult.Failure(request.RequestId, ex.Message);
        }
        finally
        {
            TryDelete(tempOutput);
        }
    }

    private async Task<OptimisationResult> RunRemuxOptimisationAsync(OptimisationRequest request, VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        context.ReportProgress(5, "Preparing remux");
        var tempOutput = FilePath.TempFile("clop-remux", plan.OutputExtension, addUniqueSuffix: true);
        tempOutput.EnsureParentDirectoryExists();

        try
        {
            var toolchainResult = await _toolchain.RemuxAsync(plan, tempOutput, context, cancellationToken).ConfigureAwait(false);
            if (!toolchainResult.Success)
            {
                return OptimisationResult.Failure(request.RequestId, toolchainResult.ErrorMessage ?? "Remux failed");
            }

            var outputPlan = OptimisedOutputPlanner.Plan(plan.SourcePath, plan.OutputExtension, request.Metadata, BuildCopyOutputPath);
            var finalOutput = outputPlan.Destination;
            finalOutput.EnsureParentDirectoryExists();
            File.Copy(tempOutput.Value, finalOutput.Value, overwrite: true);
            CopyTimestamps(plan.SourcePath, finalOutput, plan.PreserveTimestamps);

            if (outputPlan.RequiresSourceDeletion(plan.SourcePath))
            {
                TryDelete(plan.SourcePath);
            }

            var message = plan.Remux.Reason == RemuxReason.ContainerNormalisation
                ? $"Remuxed to .{plan.OutputExtension}"
                : "Skipped transcode (already optimal)";
            context.ReportProgress(100, message);
            return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, finalOutput, message);
        }
        finally
        {
            TryDelete(tempOutput);
        }
    }

    private async Task<OptimisationResult> RunAnimatedOptimisationAsync(OptimisationRequest request, VideoOptimiserPlan plan, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        context.ReportProgress(5, plan.AnimatedFormat switch
        {
            AnimatedExportFormat.Apng => "Preparing APNG export",
            AnimatedExportFormat.AnimatedWebp => "Preparing WebP export",
            _ => "Preparing GIF export"
        });

        var tempOutput = FilePath.TempFile("clop-anim", $".{plan.OutputExtension}", addUniqueSuffix: true);
        tempOutput.EnsureParentDirectoryExists();

        try
        {
            var toolchainResult = await _toolchain.ConvertToAnimatedAsync(plan, tempOutput, context, cancellationToken).ConfigureAwait(false);

            if (!toolchainResult.Success)
            {
                return OptimisationResult.Failure(request.RequestId, toolchainResult.ErrorMessage ?? "Animated export failed");
            }

            if (!File.Exists(tempOutput.Value))
            {
                return OptimisationResult.Failure(request.RequestId, "Animated export produced no output file");
            }

            var outputPlan = OptimisedOutputPlanner.Plan(plan.SourcePath, plan.OutputExtension, request.Metadata, BuildCopyOutputPath);
            var finalOutput = outputPlan.Destination;
            finalOutput.EnsureParentDirectoryExists();
            var originalSize = SafeFileSize(plan.SourcePath);
            var optimisedSize = SafeFileSize(tempOutput);

            File.Copy(tempOutput.Value, finalOutput.Value, overwrite: true);
            CopyTimestamps(plan.SourcePath, finalOutput, plan.PreserveTimestamps);

            if (outputPlan.RequiresSourceDeletion(plan.SourcePath))
            {
                TryDelete(plan.SourcePath);
            }

            var message = DescribeImprovement(originalSize, optimisedSize, plan, isGif: plan.AnimatedFormat == AnimatedExportFormat.Gif);
            context.ReportProgress(100, message);
            return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, finalOutput, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Animated export failed: {ex.Message}");
            return OptimisationResult.Failure(request.RequestId, ex.Message);
        }
        finally
        {
            TryDelete(tempOutput);
        }
    }

    private static FilePath BuildOutputPath(VideoOptimiserPlan plan) => BuildCopyOutputPath(plan.SourcePath, plan.OutputExtension);

    private static FilePath BuildCopyOutputPath(FilePath source, string extension)
    {
        var stem = source.Stem;
        if (!stem.EndsWith(".clop", StringComparison.OrdinalIgnoreCase))
        {
            stem += ".clop";
        }

        var fileName = $"{stem}.{extension}";
        return source.Parent.Append(fileName);
    }

    private static string DescribeImprovement(long originalSize, long optimisedSize, VideoOptimiserPlan plan, bool isGif = false)
    {
        if (optimisedSize <= 0)
        {
            return isGif ? "Created GIF" : "Optimised";
        }

        if (originalSize <= 0)
        {
            return isGif ? "Created GIF" : "Optimised";
        }

        var diff = originalSize - optimisedSize;
        var summary = $"{originalSize.HumanSize()} → {optimisedSize.HumanSize()}";

        string DescribeDelta()
        {
            if (diff > 0)
            {
                return $"Saved {diff.HumanSize()} ({summary})";
            }

            if (diff < 0)
            {
                return $"Larger by {Math.Abs(diff).HumanSize()} ({summary})";
            }

            return $"No size change ({summary})";
        }

        if (diff <= 0 && plan.RequireSizeReduction)
        {
            var descriptor = DescribeDelta();
            return isGif ? $"GIF ready – {descriptor}" : descriptor;
        }

        var successDescriptor = DescribeDelta();
        return isGif ? $"GIF ready – {successDescriptor}" : successDescriptor;
    }

    private static void CopyTimestamps(FilePath source, FilePath destination, bool preserve)
    {
        if (!preserve)
        {
            return;
        }

        try
        {
            var sourceInfo = new FileInfo(source.Value);
            if (!sourceInfo.Exists)
            {
                return;
            }

            File.SetCreationTimeUtc(destination.Value, sourceInfo.CreationTimeUtc);
            File.SetLastWriteTimeUtc(destination.Value, sourceInfo.LastWriteTimeUtc);
        }
        catch
        {
            // timestamp preservation is best-effort only.
        }
    }

    private static long SafeFileSize(FilePath path)
    {
        try
        {
            return File.Exists(path.Value) ? new FileInfo(path.Value).Length : 0L;
        }
        catch
        {
            return 0L;
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
        catch
        {
            // ignore cleanup failures
        }
    }

    private static VideoOptimiserPlan BuildPlan(OptimisationRequest request, VideoOptimiserOptions options, VideoProbeInfo? probeInfo)
    {
        var metadata = request.Metadata;
        var mode = ParseMode(metadata, options);
        var forceMp4 = ReadBool(metadata, "video.forceMp4") ?? options.ForceMp4;
        var removeAudio = ReadBool(metadata, "video.removeAudio") ?? options.RemoveAudio;
        var capFps = ReadBool(metadata, "video.capFps") ?? options.CapFps;
        var targetFps = Math.Max(options.MinFps, ReadInt(metadata, "video.targetFps") ?? options.TargetFps);
        var playbackSpeed = ReadDouble(metadata, "video.playbackSpeed");
        var maxWidth = ReadInt(metadata, "video.maxWidth");
        var maxHeight = ReadInt(metadata, "video.maxHeight");
        var aggressive = ReadBool(metadata, "video.aggressive") ?? options.AggressiveQuality;
        var gifWidth = ReadInt(metadata, "video.gifMaxWidth") ?? options.GifMaxWidth;
        var gifFps = ReadInt(metadata, "video.gifFps") ?? options.GifFps;
        var gifQuality = ReadInt(metadata, "video.gifQuality") ?? options.GifQuality;
        var requestedExtension = ReadString(metadata, "video.extension") ?? request.SourcePath.Extension ?? options.DefaultVideoExtension;
        requestedExtension = string.IsNullOrWhiteSpace(requestedExtension)
            ? options.DefaultVideoExtension
            : requestedExtension.Trim().TrimStart('.');

        var encoderPreset = ParseEncoderPreset(metadata, options.EncoderPreset);

        var codecOverride = ParseCodec(ReadString(metadata, "video.codec"));

        if (options.EnableFormatSpecificTuning && probeInfo?.Video is not null)
        {
            ApplyFormatSpecificHeuristics(request, probeInfo, options, ref forceMp4, ref requestedExtension, ref codecOverride, aggressive);
        }

        var animatedFormat = DetermineAnimatedFormat(mode, metadata, options);
        var hardware = options.HardwareOverride ?? VideoHardwareDetector.Detect(options.FfmpegPath, options.ProbeHardwareCapabilities);
        var encoderSelection = SelectEncoder(options, hardware, aggressive, forceMp4, requestedExtension, codecOverride, encoderPreset);
        encoderSelection = ApplyHardwareBitrateTarget(options, encoderSelection, probeInfo);

        var outputExtension = mode == VideoOptimiserMode.Gif
            ? ResolveAnimatedExtension(animatedFormat)
            : ResolveVideoExtension(encoderSelection, requestedExtension, forceMp4);

        var frameDecimation = BuildFrameDecimationPlan(options, ReadBool(metadata, "video.frameDecimation"), mode);
        var filters = BuildFilters(maxWidth, maxHeight, playbackSpeed, options.EnforceEvenDimensions, capFps, targetFps, frameDecimation);

        var lookahead = DetermineLookahead(options, encoderSelection);
        var useTwoPass = ShouldUseTwoPass(options, encoderSelection, aggressive, probeInfo);

        var audioPlan = BuildAudioPlan(metadata, options, removeAudio, outputExtension, encoderSelection);
        var softwareFallback = encoderSelection.UseHardwareEncoder
            ? CreateSoftwareSelection(options, encoderSelection.Codec, aggressive, forceMp4, requestedExtension)
            : null;

        var videoCodecArgs = BuildVideoCodecArguments(options, encoderSelection, aggressive, lookahead);

        var plan = new VideoOptimiserPlan(
            request.SourcePath,
            mode,
            outputExtension,
            forceMp4,
            audioPlan.RemoveAudio,
            capFps,
            targetFps,
            playbackSpeed,
            maxWidth,
            maxHeight,
            filters,
            encoderSelection.UseHardwareEncoder,
            aggressive,
            options.StripMetadata,
            options.PreserveTimestamps,
            options.RequireSmallerSize,
            gifWidth,
            gifFps,
            gifQuality,
            encoderSelection,
            softwareFallback,
            videoCodecArgs,
            audioPlan,
            frameDecimation,
            animatedFormat,
            useTwoPass,
            lookahead,
            options.EnableSceneCutAwareBitrate && encoderSelection.SceneCutAware,
            probeInfo,
            RemuxPlan.Disabled);

        if (options.EnableContainerAwareRemux)
        {
            var remux = DetermineRemuxPlan(plan, options, probeInfo);
            plan = plan with { Remux = remux };
        }

        return plan;
    }

    private static VideoEncoderPreset ParseEncoderPreset(IReadOnlyDictionary<string, object?> metadata, VideoEncoderPreset fallback)
    {
        var raw = ReadString(metadata, "video.encoderPreset");
        if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<VideoEncoderPreset>(raw, ignoreCase: true, out var preset))
        {
            return preset;
        }

        return fallback;
    }

    private static void ApplyFormatSpecificHeuristics(OptimisationRequest request, VideoProbeInfo probeInfo, VideoOptimiserOptions options, ref bool forceMp4, ref string requestedExtension, ref VideoCodec? codecOverride, bool aggressive)
    {
        var sourceExtension = request.SourcePath.Extension?.TrimStart('.').ToLowerInvariant() ?? string.Empty;
        var codec = probeInfo.NormalizedVideoCodec;

        if (string.Equals(sourceExtension, "webm", StringComparison.OrdinalIgnoreCase) || codec == "vp9")
        {
            forceMp4 = false;
            requestedExtension = "webm";
            codecOverride ??= VideoCodec.Vp9;
            return;
        }

        if (codec is "prores" or "dnx")
        {
            forceMp4 = false;
            if (!string.IsNullOrWhiteSpace(sourceExtension))
            {
                requestedExtension = sourceExtension;
            }
            codecOverride ??= aggressive ? VideoCodec.Av1 : VideoCodec.Hevc;
        }
    }

    private static RemuxPlan DetermineRemuxPlan(VideoOptimiserPlan plan, VideoOptimiserOptions options, VideoProbeInfo? probeInfo)
    {
        if (plan.Mode == VideoOptimiserMode.Gif || probeInfo?.Video is null)
        {
            return RemuxPlan.Disabled;
        }

        if (plan.RequiresFilters || plan.FrameDecimation.Enabled || plan.PlaybackSpeedFactor.HasValue)
        {
            return RemuxPlan.Disabled;
        }

        if (plan.RemoveAudio || !plan.Audio.CopyStream || plan.Audio.NormalizeLoudness)
        {
            return RemuxPlan.Disabled;
        }

        var targetExtension = plan.OutputExtension.Trim().ToLowerInvariant();
        var sourceExtension = plan.SourcePath.Extension?.TrimStart('.').ToLowerInvariant() ?? string.Empty;
        var videoCodec = probeInfo.NormalizedVideoCodec;
        var audioCodec = probeInfo.NormalizedAudioCodec;

        if (!IsCopyCompatible(targetExtension, videoCodec, audioCodec))
        {
            return RemuxPlan.Disabled;
        }

        var containerChanged = !string.Equals(targetExtension, sourceExtension, StringComparison.OrdinalIgnoreCase);
        if (!containerChanged && !ShouldSkipTranscodeForSavings(plan, probeInfo, options))
        {
            return RemuxPlan.Disabled;
        }

        return new RemuxPlan(true, containerChanged ? RemuxReason.ContainerNormalisation : RemuxReason.MinimalSavings);
    }

    private static bool ShouldSkipTranscodeForSavings(VideoOptimiserPlan plan, VideoProbeInfo probeInfo, VideoOptimiserOptions options)
    {
        if (options.MinimumSavingsPercentBeforeReencode <= 0)
        {
            return false;
        }

        var targetCodec = CodecVocabulary.NormalizeVideo(plan.Encoder.Codec);
        var sameCodec = string.Equals(targetCodec, probeInfo.NormalizedVideoCodec, StringComparison.OrdinalIgnoreCase);
        if (!sameCodec)
        {
            return false;
        }

        return !plan.RequiresFilters
            && !plan.FrameDecimation.Enabled
            && !plan.PlaybackSpeedFactor.HasValue
            && !plan.RemoveAudio
            && plan.Audio.CopyStream
            && !plan.Audio.NormalizeLoudness;
    }

    private static bool IsCopyCompatible(string container, string videoCodec, string audioCodec)
    {
        return container switch
        {
            "mp4" or "m4v" => (videoCodec is "h264" or "hevc") && (string.IsNullOrEmpty(audioCodec) || audioCodec == "aac"),
            "mov" => videoCodec is "h264" or "hevc" or "prores" or "dnx",
            "mkv" => !string.IsNullOrEmpty(videoCodec),
            "webm" => (videoCodec is "vp9" or "vp8") && (string.IsNullOrEmpty(audioCodec) || audioCodec is "opus" or "vorbis"),
            _ => false
        };
    }

    private static IReadOnlyList<string> BuildFilters(int? maxWidth, int? maxHeight, double? playbackSpeed, bool enforceEven, bool capFps, int targetFps, FrameDecimationPlan frameDecimation)
    {
        var filters = new List<string>();

        if (frameDecimation.Enabled)
        {
            filters.Add($"mpdecimate=hi={frameDecimation.HighThreshold.ToInvariantString()}:lo={frameDecimation.LowThreshold.ToInvariantString()}:max={frameDecimation.MaxDurationDifference.ToInvariantString()}");
        }

        if (maxWidth.HasValue || maxHeight.HasValue)
        {
            var width = maxWidth.HasValue ? EnsureEven(maxWidth.Value, enforceEven) : "-2";
            var height = maxHeight.HasValue ? EnsureEven(maxHeight.Value, enforceEven) : "-2";
            filters.Add($"scale=w={width}:h={height}:force_original_aspect_ratio=decrease");
        }

        if (playbackSpeed.HasValue && Math.Abs(playbackSpeed.Value - 1d) > 0.0001)
        {
            var factor = playbackSpeed.Value.ToString(CultureInfo.InvariantCulture);
            filters.Add($"setpts=PTS/{factor}");
        }

        if (capFps && targetFps > 0)
        {
            filters.Add($"fps={targetFps}");
        }

        return filters;
    }

    private static string EnsureEven(int value, bool enforceEven)
    {
        return enforceEven ? value.EvenInt().ToInvariantString() : value.ToInvariantString();
    }

    private static VideoCodec? ParseCodec(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "h264" or "avc" => VideoCodec.H264,
            "h265" or "hevc" => VideoCodec.Hevc,
            "av1" => VideoCodec.Av1,
            "vp9" => VideoCodec.Vp9,
            _ => null
        };
    }

    private static AnimatedExportFormat DetermineAnimatedFormat(VideoOptimiserMode mode, IReadOnlyDictionary<string, object?> metadata, VideoOptimiserOptions options)
    {
        if (mode != VideoOptimiserMode.Gif)
        {
            return AnimatedExportFormat.Gif;
        }

        var requested = ReadString(metadata, "video.animatedFormat") ?? ReadString(metadata, "video.animated") ?? ReadString(metadata, "video.gifFormat");
        if (requested is not null && TryParseAnimatedFormat(requested, out var parsed))
        {
            return parsed;
        }

        if (options.PreferAnimatedWebpForHighQuality && (ReadBool(metadata, "video.aggressive") ?? options.AggressiveQuality))
        {
            return AnimatedExportFormat.AnimatedWebp;
        }

        return options.PreferredAnimatedExport;
    }

    private static bool TryParseAnimatedFormat(string value, out AnimatedExportFormat format)
    {
        format = AnimatedExportFormat.Gif;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "gif":
            case "animated-gif":
                format = AnimatedExportFormat.Gif;
                return true;
            case "apng":
                format = AnimatedExportFormat.Apng;
                return true;
            case "webp":
            case "animated-webp":
                format = AnimatedExportFormat.AnimatedWebp;
                return true;
            default:
                return false;
        }
    }

    private static string ResolveAnimatedExtension(AnimatedExportFormat format) => format switch
    {
        AnimatedExportFormat.Apng => "apng",
        AnimatedExportFormat.AnimatedWebp => "webp",
        _ => "gif"
    };

    private static string ResolveVideoExtension(VideoEncoderSelection selection, string requested, bool forceMp4)
    {
        if (forceMp4)
        {
            return "mp4";
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            return selection.ContainerExtension;
        }

        var candidate = requested.Trim().ToLowerInvariant();
        return IsCodecCompatibleWithExtension(selection.Codec, candidate)
            ? candidate
            : selection.ContainerExtension;
    }

    private static bool IsCodecCompatibleWithExtension(VideoCodec codec, string extension)
    {
        return extension switch
        {
            "mp4" or "mov" or "m4v" => codec is VideoCodec.H264 or VideoCodec.Hevc,
            "mkv" => true,
            "webm" => codec == VideoCodec.Vp9,
            _ => true
        };
    }

    private static FrameDecimationPlan BuildFrameDecimationPlan(VideoOptimiserOptions options, bool? overrideValue, VideoOptimiserMode mode)
    {
        if (mode == VideoOptimiserMode.Gif)
        {
            return FrameDecimationPlan.Disabled;
        }

        var enabled = overrideValue ?? options.EnableFrameDecimation;
        return enabled
            ? new FrameDecimationPlan(true, options.FrameDecimationHighThreshold, options.FrameDecimationLowThreshold, options.FrameDecimationMaxDifference)
            : FrameDecimationPlan.Disabled;
    }

    private static int? DetermineLookahead(VideoOptimiserOptions options, VideoEncoderSelection encoderSelection)
    {
        if (!options.EnableSceneCutAwareBitrate)
        {
            return null;
        }

        if (encoderSelection.SuggestedLookaheadFrames <= 0)
        {
            return null;
        }

        var baseline = encoderSelection.UseHardwareEncoder ? options.HardwareLookaheadFrames : options.SoftwareLookaheadFrames;
        return Math.Max(0, Math.Min(baseline, encoderSelection.SuggestedLookaheadFrames));
    }

    private static bool ShouldUseTwoPass(VideoOptimiserOptions options, VideoEncoderSelection selection, bool aggressive, VideoProbeInfo? probeInfo)
    {
        if (!options.EnableTwoPassEncoding)
        {
            return false;
        }

        if (selection.UseHardwareEncoder || !selection.SupportsTwoPass)
        {
            return false;
        }

        if (options.TwoPassMinimumDurationSeconds > 0
            && probeInfo?.DurationSeconds is double duration
            && duration < options.TwoPassMinimumDurationSeconds)
        {
            return false;
        }

        return selection.SuggestTwoPass || aggressive;
    }

    private static VideoOptimiserMode ParseMode(IReadOnlyDictionary<string, object?> metadata, VideoOptimiserOptions options)
    {
        if (ReadString(metadata, "video.mode") is { } value && options.GifTriggers.Any(trigger => trigger.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return VideoOptimiserMode.Gif;
        }
        return VideoOptimiserMode.Video;
    }

    private static AudioPlan BuildAudioPlan(IReadOnlyDictionary<string, object?> metadata, VideoOptimiserOptions options, bool removeAudio, string outputExtension, VideoEncoderSelection encoder)
    {
        if (removeAudio)
        {
            return AudioPlan.Remove;
        }

        var codecOverride = ReadString(metadata, "video.audioCodec")?.Trim();
        var bitrate = ReadInt(metadata, "video.audioBitrate") ?? options.AudioTargetBitrateKbps;
        var channels = ReadInt(metadata, "video.audioChannels") ?? options.AudioDownmixChannels;
        var normalize = ReadBool(metadata, "video.audioNormalize") ?? options.EnableAudioNormalization;

        if (codecOverride is not null && codecOverride.Equals("copy", StringComparison.OrdinalIgnoreCase) && !normalize)
        {
            return AudioPlan.Copy;
        }

        var targetEncoder = DetermineAudioEncoder(outputExtension, codecOverride, options);
        var loudness = new LoudnessProfile(options.LoudnessTargetIntegrated, options.LoudnessTargetTruePeak, options.LoudnessTargetLra);

        var reencodeRequested = normalize
            || codecOverride is not null
            || !outputExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase)
            || encoder.Codec == VideoCodec.Av1
            || encoder.Codec == VideoCodec.Vp9;

        if (!reencodeRequested)
        {
            return AudioPlan.Copy;
        }

        return new AudioPlan(false, false, targetEncoder, bitrate, channels, normalize, loudness);
    }

    private static string DetermineAudioEncoder(string extension, string? overrideValue, VideoOptimiserOptions options)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return overrideValue.Trim().ToLowerInvariant() switch
            {
                "opus" => options.AudioEncoderOpus,
                "aac" or "mp4a" => options.AudioEncoderAac,
                "copy" => options.AudioEncoderAac,
                _ => overrideValue
            };
        }

        return extension.Equals("webm", StringComparison.OrdinalIgnoreCase) ? options.AudioEncoderOpus : options.AudioEncoderAac;
    }

    internal static IReadOnlyList<string> BuildVideoCodecArguments(VideoOptimiserOptions options, VideoEncoderSelection selection, bool aggressive, int? lookahead)
    {
        var args = new List<string>();

        var usingBitrateTarget = selection.TargetBitrateKbps.HasValue;

        if (selection.UseHardwareEncoder)
        {
            if (!string.IsNullOrWhiteSpace(selection.HardwareAcceleration))
            {
                args.Add("-hwaccel");
                args.Add(selection.HardwareAcceleration!);
            }

            if (!string.IsNullOrWhiteSpace(selection.HardwareOutputFormat))
            {
                args.Add("-hwaccel_output_format");
                args.Add(selection.HardwareOutputFormat!);
            }

            args.Add("-c:v");
            args.Add(selection.VideoEncoder);

            if (usingBitrateTarget)
            {
                AppendBitrateArguments(args, selection.TargetBitrateKbps!.Value, options);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(selection.RateControlFlag))
                {
                    args.Add(selection.RateControlFlag);
                    var rateControlValue = selection.RateControlValueOverride ?? selection.Quality.ToInvariantString();
                    args.Add(rateControlValue);

                    if (selection.RateControlFlag.Equals("-cq", StringComparison.OrdinalIgnoreCase))
                    {
                        args.Add("-b:v");
                        args.Add("0");
                    }
                }
            }

            if (lookahead.HasValue && lookahead.Value > 0)
            {
                args.Add("-rc-lookahead");
                args.Add(lookahead.Value.ToInvariantString());
            }

            if (options.EnableSceneCutAwareBitrate && selection.SceneCutAware)
            {
                args.Add("-sc_threshold");
                args.Add(options.SceneCutThreshold.ToInvariantString());
            }
        }
        else
        {
            args.Add("-c:v");
            args.Add(selection.VideoEncoder);

            if (usingBitrateTarget)
            {
                AppendBitrateArguments(args, selection.TargetBitrateKbps!.Value, options);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(selection.RateControlFlag))
                {
                    args.Add(selection.RateControlFlag);
                    var rateControlValue = selection.RateControlValueOverride ?? selection.Quality.ToInvariantString();
                    args.Add(rateControlValue);
                }
            }

            args.Add("-preset");
            args.Add(GetSoftwarePreset(options, selection.Codec, aggressive));

            if (lookahead.HasValue && lookahead.Value > 0)
            {
                args.Add("-rc-lookahead");
                args.Add(lookahead.Value.ToInvariantString());
            }

            if (options.EnableSceneCutAwareBitrate && selection.SceneCutAware)
            {
                args.Add("-sc_threshold");
                args.Add(options.SceneCutThreshold.ToInvariantString());
            }
        }

        args.Add("-pix_fmt");
        args.Add(selection.PixelFormat);

        if (selection.AdditionalArguments.Count > 0)
        {
            args.AddRange(selection.AdditionalArguments);
        }

        return args;
    }

    private static void AppendBitrateArguments(List<string> args, int targetKbps, VideoOptimiserOptions options)
    {
        var maxrate = (int)Math.Max(targetKbps, Math.Round(targetKbps * options.HardwareBitrateMaxrateHeadroom, MidpointRounding.AwayFromZero));
        var bufsize = (int)Math.Max(targetKbps, Math.Round(targetKbps * options.HardwareBitrateBufferMultiplier, MidpointRounding.AwayFromZero));

        args.Add("-b:v");
        args.Add($"{targetKbps}k");
        args.Add("-maxrate");
        args.Add($"{maxrate}k");
        args.Add("-bufsize");
        args.Add($"{bufsize}k");
    }

    private static string GetSoftwarePreset(VideoOptimiserOptions options, VideoCodec codec, bool aggressive) => codec switch
    {
        VideoCodec.Hevc => aggressive ? options.SoftwarePresetHevcAggressive : options.SoftwarePresetHevcGentle,
        VideoCodec.Av1 => aggressive ? options.SoftwarePresetAv1Aggressive : options.SoftwarePresetAv1Gentle,
        VideoCodec.Vp9 => aggressive ? options.SoftwarePresetVp9Aggressive : options.SoftwarePresetVp9Gentle,
        _ => aggressive ? options.SoftwarePresetAggressive : options.SoftwarePresetGentle
    };

    private static VideoEncoderSelection SelectEncoder(VideoOptimiserOptions options, VideoHardwareCapabilities hardware, bool aggressive, bool forceMp4, string requestedExtension, VideoCodec? overrideCodec, VideoEncoderPreset preset)
    {
        var candidates = BuildCodecPriorityList(options, aggressive, requestedExtension, overrideCodec);
        foreach (var codec in candidates)
        {
            if (preset != VideoEncoderPreset.Cpu
                && TrySelectHardwareEncoder(options, hardware, codec, aggressive, forceMp4, requestedExtension, preset, out var hardwareSelection))
            {
                return hardwareSelection;
            }

            var softwareSelection = CreateSoftwareSelection(options, codec, aggressive, forceMp4, requestedExtension);
            if (softwareSelection is not null)
            {
                return softwareSelection;
            }
        }

        return CreateSoftwareSelection(options, VideoCodec.H264, aggressive, forceMp4, requestedExtension)
            ?? VideoEncoderSelection.Software(VideoCodec.H264, options.SoftwareEncoder, ResolveContainerForCodec(VideoCodec.H264, requestedExtension, forceMp4), "yuv420p", AdjustSoftwareCrf(options.SoftwareCrf, aggressive), Array.Empty<string>(), true, true, options.SoftwareLookaheadFrames, true);
    }

    private static IEnumerable<VideoCodec> BuildCodecPriorityList(VideoOptimiserOptions options, bool aggressive, string requestedExtension, VideoCodec? overrideCodec)
    {
        var seen = new HashSet<VideoCodec>();
        if (overrideCodec.HasValue)
        {
            seen.Add(overrideCodec.Value);
            yield return overrideCodec.Value;
        }

        if (options.PreferAv1WhenAggressive && aggressive && seen.Add(VideoCodec.Av1))
        {
            yield return VideoCodec.Av1;
        }

        if (requestedExtension.Equals("webm", StringComparison.OrdinalIgnoreCase) && options.PreferVp9ForWebm && seen.Add(VideoCodec.Vp9))
        {
            yield return VideoCodec.Vp9;
        }

        if (seen.Add(VideoCodec.Hevc))
        {
            yield return VideoCodec.Hevc;
        }

        if (seen.Add(VideoCodec.H264))
        {
            yield return VideoCodec.H264;
        }

        if (seen.Add(VideoCodec.Vp9))
        {
            yield return VideoCodec.Vp9;
        }

        if (seen.Add(VideoCodec.Av1))
        {
            yield return VideoCodec.Av1;
        }
    }

    private static double? CalculateSavingsPercent(long originalSize, long optimisedSize)
    {
        if (originalSize <= 0 || optimisedSize <= 0 || optimisedSize >= originalSize)
        {
            return null;
        }

        var diff = originalSize - optimisedSize;
        return (diff / (double)originalSize) * 100d;
    }

    private static bool ShouldRetryHardwareSavings(VideoOptimiserOptions options, VideoOptimiserPlan plan, long originalSize, long optimisedSize, int attemptCount)
    {
        if (!plan.UseHardwareAcceleration
            || !plan.RequireSizeReduction
            || plan.Encoder.TargetBitrateKbps is null
            || options.HardwareBitrateRetryLimit <= 0
            || options.HardwareMinimumSavingsPercent <= 0)
        {
            return false;
        }

        if (attemptCount >= options.HardwareBitrateRetryLimit)
        {
            return false;
        }

        var savingsPercent = CalculateSavingsPercent(originalSize, optimisedSize);
        return savingsPercent.HasValue && savingsPercent.Value < options.HardwareMinimumSavingsPercent;
    }

    private static VideoOptimiserPlan? TryCreateTightenedHardwarePlan(VideoOptimiserPlan plan, VideoOptimiserOptions options)
    {
        if (plan.Encoder.TargetBitrateKbps is not int current || current <= options.HardwareBitrateFloorKbps)
        {
            return null;
        }

        var reductionRatio = options.HardwareBitrateRetryReductionRatio;
        if (reductionRatio <= 0 || reductionRatio >= 1)
        {
            reductionRatio = 0.75;
        }

        var next = (int)Math.Round(current * reductionRatio, MidpointRounding.AwayFromZero);
        next = Math.Max(options.HardwareBitrateFloorKbps, next);

        if (next >= current)
        {
            next = Math.Max(options.HardwareBitrateFloorKbps, current - 1);
        }

        if (next <= 0 || next >= current)
        {
            return null;
        }

        var updatedEncoder = plan.Encoder with { TargetBitrateKbps = next };
        var updatedArgs = BuildVideoCodecArguments(options, updatedEncoder, plan.AggressiveQuality, plan.LookaheadFrames);
        return plan with { Encoder = updatedEncoder, VideoCodecArguments = updatedArgs };
    }

    private static VideoEncoderSelection ApplyHardwareBitrateTarget(VideoOptimiserOptions options, VideoEncoderSelection selection, VideoProbeInfo? probeInfo)
    {
        if (!selection.UseHardwareEncoder)
        {
            return selection;
        }

        var sourceBitrate = EstimateSourceBitrateKbps(probeInfo);
        int target;

        if (sourceBitrate.HasValue && sourceBitrate.Value > 0)
        {
            var scaled = (int)Math.Round(sourceBitrate.Value * options.HardwareBitrateReductionRatio, MidpointRounding.AwayFromZero);
            target = Math.Clamp(scaled, options.HardwareBitrateFloorKbps, options.HardwareBitrateCeilingKbps);
        }
        else
        {
            var fallback = (int)Math.Round(options.HardwareBitrateCeilingKbps * options.HardwareBitrateReductionRatio, MidpointRounding.AwayFromZero);
            target = Math.Clamp(fallback, options.HardwareBitrateFloorKbps, options.HardwareBitrateCeilingKbps);
        }

        return target > 0
            ? selection with { TargetBitrateKbps = target }
            : selection;
    }

    private static int? EstimateSourceBitrateKbps(VideoProbeInfo? probeInfo)
    {
        if (probeInfo is null)
        {
            return null;
        }

        if (probeInfo.Video?.Bitrate is long videoBitrate && videoBitrate > 0)
        {
            return (int)Math.Max(1, videoBitrate / 1000);
        }

        if (probeInfo.ContainerBitrate.HasValue && probeInfo.ContainerBitrate.Value > 0)
        {
            return (int)Math.Max(1, probeInfo.ContainerBitrate.Value / 1000);
        }

        if (probeInfo.ContainerSize.HasValue && probeInfo.ContainerSize.Value > 0 && probeInfo.DurationSeconds is > 0)
        {
            var bitsPerSecond = (probeInfo.ContainerSize.Value * 8d) / probeInfo.DurationSeconds.Value;
            return (int)Math.Max(1, bitsPerSecond / 1000d);
        }

        return null;
    }

    private static bool TrySelectHardwareEncoder(VideoOptimiserOptions options, VideoHardwareCapabilities hardware, VideoCodec codec, bool aggressive, bool forceMp4, string requestedExtension, VideoEncoderPreset preset, out VideoEncoderSelection selection)
    {
        selection = null!;
        if (!options.UseHardwareAcceleration || !hardware.HasAnyHardware || preset == VideoEncoderPreset.Cpu)
        {
            return false;
        }

        var container = ResolveContainerForCodec(codec, requestedExtension, forceMp4);
        int BuildHardwareQuality(VideoCodec targetCodec, int baseline, int offset = 2)
        {
            var adjusted = AdjustHardwareQuality(baseline, aggressive, offset);
            return HardwareRateControlHelper.GetQuality(targetCodec, adjusted, options, aggressive);
        }

        switch (codec)
        {
            case VideoCodec.H264:
                if (hardware.SupportsNvenc)
                {
                    var quality = BuildHardwareQuality(VideoCodec.H264, options.HardwareQuality);
                    selection = VideoEncoderSelection.Hardware(
                        VideoCodec.H264,
                        "h264_nvenc",
                        container,
                        "yuv420p",
                        "cuda",
                        "cuda",
                        quality,
                        new[] { "-preset", "p7", "-tune", "hq", "-rc", "vbr_hq" },
                        supportsTwoPass: false,
                        suggestedLookahead: 12,
                        sceneCutAware: true);
                    return true;
                }
                if (hardware.SupportsAmf)
                {
                    var quality = BuildHardwareQuality(VideoCodec.H264, options.HardwareQuality);
                    selection = VideoEncoderSelection.Hardware(VideoCodec.H264, options.HardwareEncoder, container, "yuv420p", "d3d11va", null, quality, Array.Empty<string>(), supportsTwoPass: false, suggestedLookahead: 10, sceneCutAware: true);
                    return true;
                }
                if (hardware.SupportsQsv)
                {
                    var quality = BuildHardwareQuality(VideoCodec.H264, options.HardwareQuality);
                    selection = VideoEncoderSelection.Hardware(VideoCodec.H264, "h264_qsv", container, "yuv420p", "qsv", null, quality, Array.Empty<string>(), supportsTwoPass: false, suggestedLookahead: 10, sceneCutAware: true);
                    return true;
                }
                break;
            case VideoCodec.Hevc:
                if (hardware.SupportsHevcNvenc)
                {
                    var quality = BuildHardwareQuality(VideoCodec.Hevc, options.HardwareQualityHevc, 3);
                    selection = VideoEncoderSelection.Hardware(
                        VideoCodec.Hevc,
                        "hevc_nvenc",
                        container,
                        "p010le",
                        "cuda",
                        "cuda",
                        quality,
                        new[] { "-preset", "p7", "-tune", "hq", "-rc", "vbr_hq" },
                        supportsTwoPass: false,
                        suggestedLookahead: 12,
                        sceneCutAware: true);
                    return true;
                }
                if (hardware.SupportsHevcAmf)
                {
                    var quality = BuildHardwareQuality(VideoCodec.Hevc, options.HardwareQualityHevc, 3);
                    var amdProfile = ResolveAmdHevcProfile(preset);
                    selection = VideoEncoderSelection.Hardware(
                        VideoCodec.Hevc,
                        options.HardwareEncoderHevc,
                        container,
                        "p010le",
                        "d3d11va",
                        null,
                        quality,
                        amdProfile.Arguments,
                        supportsTwoPass: false,
                        suggestedLookahead: 0,
                        sceneCutAware: false,
                        rateControlFlag: amdProfile.RateControlFlag,
                        rateControlValueOverride: amdProfile.RateControlValueOverride);
                    return true;
                }
                if (hardware.SupportsHevcQsv)
                {
                    var quality = BuildHardwareQuality(VideoCodec.Hevc, options.HardwareQualityHevc, 3);
                    selection = VideoEncoderSelection.Hardware(VideoCodec.Hevc, "hevc_qsv", container, "p010le", "qsv", null, quality, Array.Empty<string>(), supportsTwoPass: false, suggestedLookahead: 10, sceneCutAware: true);
                    return true;
                }
                break;
            case VideoCodec.Av1:
                if (hardware.SupportsAv1Nvenc)
                {
                    var quality = BuildHardwareQuality(VideoCodec.Av1, options.HardwareQualityAv1, 4);
                    selection = VideoEncoderSelection.Hardware(
                        VideoCodec.Av1,
                        "av1_nvenc",
                        container,
                        "p010le",
                        "cuda",
                        "cuda",
                        quality,
                        new[] { "-preset", "p7", "-tune", "hq", "-rc", "vbr_hq" },
                        supportsTwoPass: false,
                        suggestedLookahead: 14,
                        sceneCutAware: true);
                    return true;
                }
                if (hardware.SupportsAv1Amf)
                {
                    var quality = BuildHardwareQuality(VideoCodec.Av1, options.HardwareQualityAv1, 4);
                    selection = VideoEncoderSelection.Hardware(VideoCodec.Av1, options.HardwareEncoderAv1, container, "p010le", "d3d11va", null, quality, Array.Empty<string>(), supportsTwoPass: false, suggestedLookahead: 12, sceneCutAware: true);
                    return true;
                }
                if (hardware.SupportsAv1Qsv)
                {
                    var quality = BuildHardwareQuality(VideoCodec.Av1, options.HardwareQualityAv1, 4);
                    selection = VideoEncoderSelection.Hardware(VideoCodec.Av1, "av1_qsv", container, "p010le", "qsv", null, quality, Array.Empty<string>(), supportsTwoPass: false, suggestedLookahead: 12, sceneCutAware: true);
                    return true;
                }
                break;
            case VideoCodec.Vp9:
                if (hardware.SupportsQsv)
                {
                    var quality = BuildHardwareQuality(VideoCodec.Vp9, options.HardwareQualityVp9, 4);
                    selection = VideoEncoderSelection.Hardware(VideoCodec.Vp9, "vp9_qsv", container, "yuv420p", "qsv", null, quality, Array.Empty<string>(), supportsTwoPass: false, suggestedLookahead: 10, sceneCutAware: false);
                    return true;
                }
                break;
        }

        return false;
    }

    private static (IReadOnlyList<string> Arguments, string RateControlFlag, string? RateControlValueOverride) ResolveAmdHevcProfile(VideoEncoderPreset preset)
    {
        return preset switch
        {
            VideoEncoderPreset.GpuSimple => (new[]
            {
                "-quality", "balanced",
                "-usage", "transcoding",
                "-profile:v", "main10",
                "-level", "5.1",
                "-bf", "2"
            }, "-rc", "vbr"),
            VideoEncoderPreset.GpuCqp => (new[]
            {
                "-quality", "quality",
                "-usage", "transcoding",
                "-profile:v", "main10",
                "-level", "5.1",
                "-qp_i", "22",
                "-qp_p", "24",
                "-qp_b", "26",
                "-bf", "3"
            }, "-rc", "cqp"),
            _ => (new[]
            {
                "-quality", "quality",
                "-usage", "transcoding",
                "-profile:v", "main10",
                "-level", "5.1",
                "-bf", "3"
            }, "-rc", "vbr")
        };
    }

    private static int AdjustHardwareQuality(int baseline, bool aggressive, int offset = 2)
    {
        return aggressive ? Math.Max(0, baseline - offset) : baseline;
    }


    private static string ResolveContainerForCodec(VideoCodec codec, string requestedExtension, bool forceMp4)
    {
        var defaultContainer = codec switch
        {
            VideoCodec.Vp9 => "webm",
            VideoCodec.Av1 => "mkv",
            _ => "mp4"
        };

        if (forceMp4)
        {
            return "mp4";
        }

        if (string.IsNullOrWhiteSpace(requestedExtension))
        {
            return defaultContainer;
        }

        return IsCodecCompatibleWithExtension(codec, requestedExtension) ? requestedExtension : defaultContainer;
    }

    private static VideoEncoderSelection? CreateSoftwareSelection(VideoOptimiserOptions options, VideoCodec codec, bool aggressive, bool forceMp4, string requestedExtension)
    {
        var container = ResolveContainerForCodec(codec, requestedExtension, forceMp4);
        return codec switch
        {
            VideoCodec.H264 => VideoEncoderSelection.Software(VideoCodec.H264, options.SoftwareEncoder, container, "yuv420p", AdjustSoftwareCrf(options.SoftwareCrf, aggressive), additionalArgs: Array.Empty<string>(), supportsTwoPass: true, suggestTwoPass: true, suggestedLookahead: options.SoftwareLookaheadFrames, sceneCutAware: true),
            VideoCodec.Hevc => VideoEncoderSelection.Software(VideoCodec.Hevc, options.SoftwareEncoderHevc, container, "yuv420p10le", AdjustSoftwareCrf(options.SoftwareCrfHevc, aggressive, 3), additionalArgs: Array.Empty<string>(), supportsTwoPass: true, suggestTwoPass: true, suggestedLookahead: options.SoftwareLookaheadFrames, sceneCutAware: true),
            VideoCodec.Av1 => VideoEncoderSelection.Software(VideoCodec.Av1, options.SoftwareEncoderAv1, container, "yuv420p10le", AdjustSoftwareCrf(options.SoftwareCrfAv1, aggressive, 6), additionalArgs: Array.Empty<string>(), supportsTwoPass: true, suggestTwoPass: aggressive, suggestedLookahead: options.SoftwareLookaheadFrames, sceneCutAware: true),
            VideoCodec.Vp9 => VideoEncoderSelection.Software(VideoCodec.Vp9, options.SoftwareEncoderVp9, container, "yuv420p", AdjustSoftwareCrf(options.SoftwareCrfVp9, aggressive, 5), additionalArgs: new[] { "-b:v", "0", "-row-mt", "1" }, supportsTwoPass: true, suggestTwoPass: true, suggestedLookahead: options.SoftwareLookaheadFrames, sceneCutAware: false),
            _ => null
        };
    }

    private static int AdjustSoftwareCrf(int baseline, bool aggressive, int offset = 4)
    {
        return aggressive ? Math.Max(0, baseline - offset) : baseline;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!TryGetMetadataValue(metadata, key, out var raw))
        {
            return null;
        }

        return raw switch
        {
            bool value => value,
            string str when bool.TryParse(str, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False => element.GetBoolean(),
            _ => null
        };
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!TryGetMetadataValue(metadata, key, out var raw))
        {
            return null;
        }

        return raw switch
        {
            int value => value,
            long value => (int)value,
            double value => (int)Math.Round(value, MidpointRounding.AwayFromZero),
            string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.TryGetInt32(out var parsed) ? parsed : null,
            _ => null
        };
    }

    private static double? ReadDouble(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!TryGetMetadataValue(metadata, key, out var raw))
        {
            return null;
        }

        return raw switch
        {
            double value => value,
            float value => value,
            int value => value,
            long value => value,
            string str when double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetDouble(),
            _ => null
        };
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!TryGetMetadataValue(metadata, key, out var raw))
        {
            return null;
        }

        return raw switch
        {
            string value => value,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => raw?.ToString()
        };
    }

    private static bool TryGetMetadataValue(IReadOnlyDictionary<string, object?> metadata, string key, out object? value)
    {
        if (metadata.TryGetValue(key, out value))
        {
            return true;
        }

        if (!key.StartsWith("video.", StringComparison.OrdinalIgnoreCase))
        {
            var videoKey = $"video.{key}";
            if (metadata.TryGetValue(videoKey, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }
}

public enum VideoOptimiserMode
{
    Video,
    Gif
}

public sealed record VideoOptimiserPlan(
    FilePath SourcePath,
    VideoOptimiserMode Mode,
    string OutputExtension,
    bool ForceMp4,
    bool RemoveAudio,
    bool CapFps,
    int TargetFps,
    double? PlaybackSpeedFactor,
    int? MaxWidth,
    int? MaxHeight,
    IReadOnlyList<string> Filters,
    bool UseHardwareAcceleration,
    bool AggressiveQuality,
    bool StripMetadata,
    bool PreserveTimestamps,
    bool RequireSizeReduction,
    int GifMaxWidth,
    int GifFps,
    int GifQuality,
    VideoEncoderSelection Encoder,
    VideoEncoderSelection? SoftwareFallback,
    IReadOnlyList<string> VideoCodecArguments,
    AudioPlan Audio,
    FrameDecimationPlan FrameDecimation,
    AnimatedExportFormat AnimatedFormat,
    bool UseTwoPass,
    int? LookaheadFrames,
    bool SceneCutAware,
    VideoProbeInfo? SourceProbe,
    RemuxPlan Remux)
{
    public bool RequiresFilters => Filters.Count > 0;
    public bool ShouldRemux => Remux.Enabled;
}

public interface IVideoToolchain
{
    Task<ToolchainResult> TranscodeAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken);

    Task<ToolchainResult> ConvertToAnimatedAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken);

    Task<ToolchainResult> RemuxAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken);
}

public sealed record ToolchainResult(bool Success, string? ErrorMessage = null)
{
    public static ToolchainResult Successful(string? message = null) => new(true, message);

    public static ToolchainResult Failure(string message) => new(false, message);
}

internal sealed class ExternalVideoToolchain : IVideoToolchain
{
    private readonly VideoOptimiserOptions _options;

    public ExternalVideoToolchain(VideoOptimiserOptions options)
    {
        _options = options;
    }

    public async Task<ToolchainResult> TranscodeAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        return await RunTranscodeAsync(plan, tempOutput, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolchainResult> ConvertToAnimatedAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        return plan.AnimatedFormat switch
        {
            AnimatedExportFormat.Gif => await ConvertToGifViaPaletteAsync(plan, tempOutput, context, cancellationToken).ConfigureAwait(false),
            AnimatedExportFormat.Apng => await ConvertToApngAsync(plan, tempOutput, context, cancellationToken).ConfigureAwait(false),
            AnimatedExportFormat.AnimatedWebp => await ConvertToWebpAsync(plan, tempOutput, context, cancellationToken).ConfigureAwait(false),
            _ => ToolchainResult.Failure("Unsupported animated format")
        };
    }

    public async Task<ToolchainResult> RemuxAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var tracker = new FfmpegProgressTracker("Remuxing");
        var args = BuildRemuxArguments(plan, tempOutput);
        return await RunProcessAsync(_options.FfmpegPath, args, context, cancellationToken, line => tracker.Process(line, context)).ConfigureAwait(false);
    }

    private async Task<ToolchainResult> RunTranscodeAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var tracker = new FfmpegProgressTracker("Encoding");

        if (plan.UseTwoPass)
        {
            var firstPassOutput = FilePath.TempFile("clop-pass", ".tmp", addUniqueSuffix: true);
            var passLogBase = Path.Combine(Path.GetTempPath(), $"clop-pass-{Guid.NewGuid():N}");

            try
            {
                var firstPassArgs = BuildTranscodeArguments(plan, firstPassOutput, includeAudio: false);
                var firstInsertIndex = firstPassArgs.IndexOf("-progress");
                if (firstInsertIndex < 0)
                {
                    firstInsertIndex = firstPassArgs.Count - 1;
                }
                firstPassArgs.Insert(firstInsertIndex, "-pass");
                firstPassArgs.Insert(firstInsertIndex + 1, "1");
                firstPassArgs.Insert(firstInsertIndex + 2, "-passlogfile");
                firstPassArgs.Insert(firstInsertIndex + 3, passLogBase);

                var firstResult = await RunProcessAsync(_options.FfmpegPath, firstPassArgs, context, cancellationToken, line => tracker.Process(line, context)).ConfigureAwait(false);
                TryDelete(firstPassOutput);

                if (!firstResult.Success)
                {
                    CleanupPassLogs(passLogBase);
                    return firstResult;
                }

                var secondPassArgs = BuildTranscodeArguments(plan, tempOutput, includeAudio: true);
                var secondInsertIndex = secondPassArgs.IndexOf("-progress");
                if (secondInsertIndex < 0)
                {
                    secondInsertIndex = secondPassArgs.Count - 1;
                }
                secondPassArgs.Insert(secondInsertIndex, "-pass");
                secondPassArgs.Insert(secondInsertIndex + 1, "2");
                secondPassArgs.Insert(secondInsertIndex + 2, "-passlogfile");
                secondPassArgs.Insert(secondInsertIndex + 3, passLogBase);

                var secondResult = await RunProcessAsync(_options.FfmpegPath, secondPassArgs, context, cancellationToken, line => tracker.Process(line, context)).ConfigureAwait(false);
                CleanupPassLogs(passLogBase);

                if (secondResult.Success)
                {
                    return secondResult;
                }

                return await TrySoftwareFallbackAsync(plan, tempOutput, context, cancellationToken, tracker, secondResult).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(firstPassOutput);
            }
        }

        var args = BuildTranscodeArguments(plan, tempOutput, includeAudio: true);
        var result = await RunProcessAsync(_options.FfmpegPath, args, context, cancellationToken, line => tracker.Process(line, context)).ConfigureAwait(false);

        if (result.Success)
        {
            return result;
        }

        return await TrySoftwareFallbackAsync(plan, tempOutput, context, cancellationToken, tracker, result).ConfigureAwait(false);
    }

    private async Task<ToolchainResult> TrySoftwareFallbackAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken, FfmpegProgressTracker tracker, ToolchainResult lastResult)
    {
        if (plan.SoftwareFallback is null)
        {
            return lastResult;
        }

        Log.Info("Hardware video encoder failed; falling back to software encoder.");
        context.ReportProgress(5, "Switching to software encoder");

        TryDelete(tempOutput);

        var fallbackArgs = VideoOptimiser.BuildVideoCodecArguments(_options, plan.SoftwareFallback, plan.AggressiveQuality, plan.LookaheadFrames);
        var fallbackPlan = plan with
        {
            Encoder = plan.SoftwareFallback,
            SoftwareFallback = null,
            UseTwoPass = false,
            UseHardwareAcceleration = plan.SoftwareFallback.UseHardwareEncoder,
            VideoCodecArguments = fallbackArgs
        };

        var args = BuildTranscodeArguments(fallbackPlan, tempOutput, includeAudio: true);
        return await RunProcessAsync(_options.FfmpegPath, args, context, cancellationToken, line => tracker.Process(line, context)).ConfigureAwait(false);
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
        catch
        {
            // best effort cleanup
        }
    }

    private static void CleanupPassLogs(string passLogBase)
    {
        try
        {
            var directory = Path.GetDirectoryName(passLogBase) ?? Path.GetTempPath();
            var prefix = Path.GetFileName(passLogBase);
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, $"{prefix}*"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    private List<string> BuildTranscodeArguments(VideoOptimiserPlan plan, FilePath output, bool includeAudio)
    {
        var args = new List<string>
        {
            "-y"
        };

        var (inputScoped, encoderScoped) = PartitionCodecArguments(plan.VideoCodecArguments);
        if (inputScoped.Count > 0)
        {
            args.AddRange(inputScoped);
        }

        args.AddRange(new[] { "-i", plan.SourcePath.Value });

        if (plan.RequiresFilters)
        {
            args.Add("-vf");
            args.Add(string.Join(',', plan.Filters));
        }

        args.AddRange(encoderScoped);

        args.AddRange(new[] { "-map", "0:v" });

        if (includeAudio)
        {
            AppendAudioArguments(plan, args);
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(new[] { "-movflags", "+faststart", "-progress", "pipe:2", "-nostats", "-hide_banner", "-stats_period", "0.2", output.Value });
        return args;
    }

    private static (List<string> InputScoped, List<string> EncoderScoped) PartitionCodecArguments(IReadOnlyList<string> arguments)
    {
        var inputScoped = new List<string>();
        var encoderScoped = new List<string>();

        for (var i = 0; i < arguments.Count; i++)
        {
            var flag = arguments[i];
            if (IsInputScopedCodecOption(flag))
            {
                inputScoped.Add(flag);
                if (i + 1 < arguments.Count)
                {
                    inputScoped.Add(arguments[++i]);
                }
                continue;
            }

            encoderScoped.Add(flag);
        }

        return (inputScoped, encoderScoped);
    }

    private static bool IsInputScopedCodecOption(string value)
    {
        return value.Equals("-hwaccel", StringComparison.OrdinalIgnoreCase)
            || value.Equals("-hwaccel_output_format", StringComparison.OrdinalIgnoreCase)
            || value.Equals("-init_hw_device", StringComparison.OrdinalIgnoreCase);
    }

    private List<string> BuildRemuxArguments(VideoOptimiserPlan plan, FilePath output)
    {
        var args = new List<string>
        {
            "-y",
            "-i", plan.SourcePath.Value,
            "-c", "copy",
            "-map", "0"
        };

        if (plan.StripMetadata)
        {
            args.AddRange(new[] { "-map_metadata", "-1" });
        }

        if (plan.OutputExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase) || plan.OutputExtension.Equals("m4v", StringComparison.OrdinalIgnoreCase) || plan.OutputExtension.Equals("mov", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(new[] { "-movflags", "+faststart" });
        }

        args.AddRange(new[] { "-progress", "pipe:2", "-nostats", "-hide_banner", "-stats_period", "0.2", output.Value });
        return args;
    }

    private static void AppendAudioArguments(VideoOptimiserPlan plan, List<string> args)
    {
        if (plan.Audio.RemoveAudio)
        {
            args.Add("-an");
            return;
        }

        if (plan.Audio.CopyStream)
        {
            args.AddRange(new[] { "-c:a", "copy", "-map", "0:a?" });
            return;
        }

        var encoder = plan.Audio.Encoder ?? "aac";
        args.AddRange(new[] { "-c:a", encoder });

        if (plan.Audio.BitrateKbps.HasValue)
        {
            args.Add("-b:a");
            args.Add($"{plan.Audio.BitrateKbps.Value}k");
        }

        if (plan.Audio.Channels.HasValue)
        {
            args.Add("-ac");
            args.Add(plan.Audio.Channels.Value.ToInvariantString());
        }

        if (plan.Audio.NormalizeLoudness)
        {
            var profile = plan.Audio.Loudness;
            args.Add("-af");
            args.Add($"loudnorm=I={profile.Integrated.ToInvariantString()}:TP={profile.TruePeak.ToInvariantString()}:LRA={profile.LoudnessRange.ToInvariantString()}:print_format=summary");
        }

        args.AddRange(new[] { "-map", "0:a?" });
    }

    private async Task<ToolchainResult> ConvertToGifViaPaletteAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var tracker = new FfmpegProgressTracker("Encoding GIF");
        var filterGraph = BuildGifFilterGraph(plan);
        var args = new List<string> { "-y", "-i", plan.SourcePath.Value, "-vf", filterGraph, "-loop", "0", "-progress", "pipe:2", "-nostats", "-hide_banner", "-stats_period", "0.2", tempOutput.Value };
        return await RunProcessAsync(_options.FfmpegPath, args, context, cancellationToken, line => tracker.Process(line, context)).ConfigureAwait(false);
    }

    private async Task<ToolchainResult> ConvertToApngAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var tracker = new FfmpegProgressTracker("Encoding APNG");
        var filter = BuildAnimatedFilterChain(plan);
        var args = new List<string> { "-y", "-i", plan.SourcePath.Value };
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.AddRange(new[] { "-vf", filter });
        }
        args.AddRange(new[] { "-plays", "0", "-progress", "pipe:2", "-nostats", "-hide_banner", "-stats_period", "0.2", tempOutput.Value });
        return await RunProcessAsync(_options.FfmpegPath, args, context, cancellationToken, line => tracker.Process(line, context)).ConfigureAwait(false);
    }

    private async Task<ToolchainResult> ConvertToWebpAsync(VideoOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
    {
        var tracker = new FfmpegProgressTracker("Encoding WebP");
        var filter = BuildAnimatedFilterChain(plan);
        var args = new List<string> { "-y", "-i", plan.SourcePath.Value };
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.AddRange(new[] { "-vf", filter });
        }
        args.AddRange(new[]
        {
            "-c:v", "libwebp_anim",
            "-loop", "0",
            "-quality", plan.GifQuality.ToInvariantString(),
            "-compression_level", "6",
            "-progress", "pipe:2",
            "-nostats",
            "-hide_banner",
            "-stats_period", "0.2",
            tempOutput.Value
        });
        return await RunProcessAsync(_options.FfmpegPath, args, context, cancellationToken, line => tracker.Process(line, context)).ConfigureAwait(false);
    }

    private static string BuildGifFilterGraph(VideoOptimiserPlan plan)
    {
        var baseFilters = BuildBaseAnimationFilters(plan);
        var chain = baseFilters.Count > 0 ? string.Join(',', baseFilters) + "," : string.Empty;
        return chain + "split[s0][s1];[s0]palettegen=stats_mode=full[p];[s1][p]paletteuse=dither=bayer";
    }

    private static string BuildAnimatedFilterChain(VideoOptimiserPlan plan)
    {
        var filters = BuildBaseAnimationFilters(plan);
        return filters.Count == 0 ? string.Empty : string.Join(',', filters);
    }

    private static List<string> BuildBaseAnimationFilters(VideoOptimiserPlan plan)
    {
        var filters = new List<string>();
        if (plan.Filters.Count > 0)
        {
            filters.AddRange(plan.Filters);
        }

        var scaleFilter = $"scale=w={plan.GifMaxWidth}:h=-2:force_original_aspect_ratio=decrease";
        if (!filters.Any(f => f.StartsWith("scale=", StringComparison.OrdinalIgnoreCase)))
        {
            filters.Add(scaleFilter);
        }

        var fpsFilter = $"fps={plan.GifFps}";
        if (!filters.Any(f => f.StartsWith("fps=", StringComparison.OrdinalIgnoreCase)))
        {
            filters.Add(fpsFilter);
        }
        return filters;
    }

    private static async Task<ToolchainResult> RunProcessAsync(string executable, IReadOnlyList<string> arguments, OptimiserExecutionContext context, CancellationToken cancellationToken, Action<string>? progressCallback)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutCompletion.TrySetResult();
            }
            else
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrCompletion.TrySetResult();
            }
            else
            {
                stderr.AppendLine(e.Data);
                progressCallback?.Invoke(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return ToolchainResult.Failure($"Unable to start process '{executable}'");
            }
        }
        catch (Exception ex)
        {
            return ToolchainResult.Failure($"Unable to start '{executable}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignored
            }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdoutCompletion.Task, stderrCompletion.Task).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = $"Process '{executable}' exited with code {process.ExitCode}";
            Log.Error($"{error}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            return ToolchainResult.Failure(error);
        }

        return ToolchainResult.Successful();
    }

    private sealed class FfmpegProgressTracker
    {
        private static readonly Regex DurationRegex = new(@"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private readonly string _phase;
        private long? _durationUs;

        public FfmpegProgressTracker(string phase)
        {
            _phase = phase;
        }

        public void Process(string line, OptimiserExecutionContext context)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var durationMatch = DurationRegex.Match(line);
            if (durationMatch.Success)
            {
                var hours = long.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var minutes = long.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var seconds = long.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                var centiseconds = long.Parse(durationMatch.Groups[4].Value, CultureInfo.InvariantCulture);
                var totalMs = ((hours * 3600 + minutes * 60 + seconds) * 1000) + (centiseconds * 10);
                _durationUs = totalMs * 1000;
                context.ReportProgress(5, $"{_phase} – duration {FormatTime(_durationUs.Value)}");
                return;
            }

            if (line.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase) && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
            {
                var percent = _durationUs.HasValue && _durationUs.Value > 0
                    ? Math.Clamp(micros / (double)_durationUs.Value * 100d, 0d, 99.0d)
                    : Math.Min(98d, micros / 10_000_000d);
                context.ReportProgress(percent, $"{_phase} {FormatTime(micros)}");
                return;
            }

            if (line.StartsWith("progress=end", StringComparison.OrdinalIgnoreCase))
            {
                context.ReportProgress(99, "Finalising");
            }
        }

        private static string FormatTime(long microseconds)
        {
            var ts = TimeSpan.FromMilliseconds(microseconds / 1000d);
            return ts.TotalHours >= 1
                ? ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
    }

}