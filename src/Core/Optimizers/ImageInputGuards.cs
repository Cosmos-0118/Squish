using System;
using System.IO;
using System.Threading;
using ClopWindows.Core.Shared;
using SixLabors.ImageSharp;

namespace ClopWindows.Core.Optimizers;

internal static class ImageInputGuards
{
    public static (bool IsValid, string? Error) Validate(FilePath sourcePath, ImageOptimiserOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (options.MaxInputPixels is null && options.MaxDimension is null)
        {
            return (true, null);
        }

        using var stream = File.OpenRead(sourcePath.Value);
        var info = Image.Identify(stream);
        if (info is null)
        {
            return (false, "Unable to read image metadata for validation.");
        }

        var totalPixels = (long)info.Width * info.Height;
        if (options.MaxInputPixels is { } maxPixels && totalPixels > maxPixels)
        {
            var message = $"Image exceeds pixel limit: {info.Width}×{info.Height} ({totalPixels:N0} pixels) > allowed {maxPixels:N0}.";
            return (false, message);
        }

        if (options.MaxDimension is { } maxDimension && (info.Width > maxDimension || info.Height > maxDimension))
        {
            var message = $"Image exceeds dimension limit: {info.Width}×{info.Height} > {maxDimension} on at least one edge.";
            return (false, message);
        }

        return (true, null);
    }
}
