using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

internal static class PdfDocumentProbe
{
    private const double DefaultShortEdgeInches = 8.5d;
    private const double DefaultLongEdgeInches = 11d;

    private static readonly Regex PageCountRegex = new("/Count\\s+(?<count>\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ImageDictionaryRegex = new("/Subtype\\s*/Image", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ImageDimensionsRegex = new("/Subtype\\s*/Image(?<dict>.*?)>>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex WidthRegex = new("/Width\\s+(?<width>\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HeightRegex = new("/Height\\s+(?<height>\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PdfDocumentInsights GetInsights(FilePath path, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return PdfDocumentInsights.Empty;
        }

        try
        {
            using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
            var toRead = (int)Math.Min(maxBytes, stream.Length);
            if (toRead <= 0)
            {
                return PdfDocumentInsights.Empty;
            }
            var buffer = ArrayPool<byte>.Shared.Rent(toRead);

            try
            {
                var read = stream.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    return PdfDocumentInsights.Empty;
                }

                var text = Encoding.ASCII.GetString(buffer, 0, read);
                var pageCount = ExtractPageCount(text);
                var imageCount = CountImages(text);
                var maxPixels = ExtractMaxImagePixels(text);
                var estimatedDpi = EstimateImageDpi(maxPixels);
                var density = pageCount > 0 ? imageCount / (double)pageCount : imageCount;

                return new PdfDocumentInsights(
                    pageCount,
                    imageCount,
                    estimatedDpi,
                    density,
                    stream.Length,
                    maxPixels);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
        catch
        {
            return PdfDocumentInsights.Empty;
        }
    }

    private static int ExtractPageCount(string content)
    {
        var matches = PageCountRegex.Matches(content);
        if (matches.Count == 0)
        {
            return 0;
        }

        var max = 0;
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups["count"].Value, out var value) && value > max)
            {
                max = value;
            }
        }

        return max;
    }

    private static int CountImages(string content)
    {
        var matches = ImageDictionaryRegex.Matches(content);
        return matches.Count;
    }

    private static int ExtractMaxImagePixels(string content)
    {
        var matches = ImageDimensionsRegex.Matches(content);
        var max = 0;

        foreach (Match match in matches)
        {
            var dict = match.Groups["dict"].Value;
            var widthMatch = WidthRegex.Match(dict);
            var heightMatch = HeightRegex.Match(dict);

            if (!widthMatch.Success || !heightMatch.Success)
            {
                continue;
            }

            if (!int.TryParse(widthMatch.Groups["width"].Value, out var width) || width <= 0)
            {
                continue;
            }

            if (!int.TryParse(heightMatch.Groups["height"].Value, out var height) || height <= 0)
            {
                continue;
            }

            var candidate = Math.Max(width, height);
            if (candidate > max)
            {
                max = candidate;
            }
        }

        return max;
    }

    private static double EstimateImageDpi(int maxPixels)
    {
        if (maxPixels <= 0)
        {
            return 0d;
        }

        var longEdgeDpi = maxPixels / DefaultLongEdgeInches;
        var shortEdgeDpi = maxPixels / DefaultShortEdgeInches;
        return Math.Max(longEdgeDpi, shortEdgeDpi);
    }
}

public sealed record PdfDocumentInsights(
    int PageCount,
    int ImageCount,
    double EstimatedMaxImageDpi,
    double ImageDensity,
    long FileSize,
    int MaxImagePixels)
{
    public static PdfDocumentInsights Empty { get; } = new(0, 0, 0d, 0d, 0, 0);

    public bool HasData => PageCount > 0 || ImageCount > 0 || EstimatedMaxImageDpi > 0d;

    public double AverageBytesPerPage => PageCount > 0 ? FileSize / (double)PageCount : FileSize;
}
