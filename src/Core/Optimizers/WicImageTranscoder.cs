using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ClopWindows.Core.Shared;
using System.Runtime.Versioning;

namespace ClopWindows.Core.Optimizers;

[SupportedOSPlatform("windows6.1")]
internal static class WicImageTranscoder
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "bmp", "gif", "tif", "tiff"
    };

    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "bmp", "gif", "tif", "tiff"
    };

    public static bool IsSupported(string extension) => !string.IsNullOrWhiteSpace(extension) && SupportedExtensions.Contains(NormalizeExtension(extension));

    public static async Task<WicTranscodeOutcome?> TryTranscodeAsync(
        FilePath source,
        string extension,
        WicFastPathOptions options,
        int fallbackJpegQuality,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled || !IsSupported(extension))
        {
            return null;
        }

        var tcs = new TaskCompletionSource<WicTranscodeOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Run()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = TranscodeCore(source, NormalizeExtension(extension), options, fallbackJpegQuality);
                tcs.TrySetResult(outcome);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Clop WIC Fast Path"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task.ConfigureAwait(false);
    }

    private static WicTranscodeOutcome? TranscodeCore(
        FilePath source,
        string extension,
        WicFastPathOptions options,
        int fallbackJpegQuality)
    {
        var encoder = CreateEncoder(extension);
        if (encoder is null)
        {
            return null;
        }

        using var input = new FileStream(source.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        var frame = decoder.Frames[0];
        ApplyEncoderPreferences(encoder, extension, options, fallbackJpegQuality);

        BitmapSource? thumbnail = options.StripMetadata ? null : frame.Thumbnail;
        BitmapMetadata? metadata = options.StripMetadata ? null : CloneMetadata(frame.Metadata as BitmapMetadata);
        var wicFrame = BitmapFrame.Create(frame, thumbnail, metadata, frame.ColorContexts);
        encoder.Frames.Add(wicFrame);

        var temp = FilePath.TempFile("clop-wic", extension, addUniqueSuffix: true);
        temp.EnsureParentDirectoryExists();

        using (var output = new FileStream(temp.Value, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(output);
        }

        var originalSize = new FileInfo(source.Value).Length;
        var newSize = new FileInfo(temp.Value).Length;
        var savingsPercent = originalSize <= 0
            ? 0d
            : (originalSize - newSize) * 100d / originalSize;

        if (savingsPercent <= 0)
        {
            TryDelete(temp);
            return new WicTranscodeOutcome(WicTranscodeStatus.NoSavings, null, extension, savingsPercent, originalSize, newSize);
        }

        if (options.SkipLosslessWhenBelowThreshold && LosslessExtensions.Contains(extension) && savingsPercent < options.MinimumSavingsPercent)
        {
            TryDelete(temp);
            return new WicTranscodeOutcome(WicTranscodeStatus.NoSavings, null, extension, savingsPercent, originalSize, newSize);
        }

        return new WicTranscodeOutcome(WicTranscodeStatus.Success, temp, extension, savingsPercent, originalSize, newSize);
    }

    private static void ApplyEncoderPreferences(BitmapEncoder encoder, string extension, WicFastPathOptions options, int fallbackJpegQuality)
    {
        if (encoder is JpegBitmapEncoder jpegEncoder)
        {
            var requested = options.OverrideJpegQuality ?? fallbackJpegQuality;
            jpegEncoder.QualityLevel = Math.Clamp(requested, 30, 100);
            jpegEncoder.Rotation = Rotation.Rotate0;
        }
    }

    private static BitmapEncoder? CreateEncoder(string extension) => extension switch
    {
        "jpg" or "jpeg" => new JpegBitmapEncoder(),
        "png" => new PngBitmapEncoder(),
        "bmp" => new BmpBitmapEncoder(),
        "gif" => new GifBitmapEncoder(),
        "tif" or "tiff" => new TiffBitmapEncoder(),
        _ => null
    };

    private static BitmapMetadata? CloneMetadata(BitmapMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        try
        {
            return metadata.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeExtension(string extension)
    {
        var value = extension ?? string.Empty;
        value = value.Trim();
        if (value.StartsWith('.'))
        {
            value = value[1..];
        }
        return value.ToLowerInvariant();
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
            // ignore cleanup issues
        }
    }
}

internal sealed record WicTranscodeOutcome(
    WicTranscodeStatus Status,
    FilePath? OutputPath,
    string Extension,
    double SavingsPercent,
    long OriginalBytes,
    long OutputBytes);

internal enum WicTranscodeStatus
{
    Success,
    NoSavings
}
