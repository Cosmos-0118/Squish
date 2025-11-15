using System;
using System.Globalization;

namespace ClopWindows.Core.Shared;

/// <summary>
/// Numeric helpers that mirror the Swift extensions used throughout the optimisation pipeline.
/// </summary>
public static class NumericExtensions
{
    public static string ToInvariantString(this double value) => value.ToString(CultureInfo.InvariantCulture);

    public static string ToInvariantString(this int value) => value.ToString(CultureInfo.InvariantCulture);

    public static double AsDouble(this int value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    public static int Intround(this double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    public static int EvenInt(this double value)
    {
        var rounded = value.Intround();
        return rounded + rounded % 2;
    }

    public static int EvenInt(this int value) => value + value % 2;

    public static double FractionalAspectRatio(this double value) => value > 1 ? 1 / value : value;

    public static string HumanSize(this long bytes)
    {
        if (bytes < 1000)
        {
            return $"{bytes}B";
        }
        if (bytes < 1_000_000)
        {
            return $"{bytes / 1000}KB";
        }
        if (bytes < 1_000_000_000)
        {
            var value = bytes / 1_000_000d;
            return value < 10
                ? $"{value.ToString("0.0", CultureInfo.InvariantCulture)}MB"
                : $"{((int)Math.Round(value)).ToInvariantString()}MB";
        }
        var gigabytes = bytes / 1_000_000_000d;
        return gigabytes < 10
            ? $"{gigabytes.ToString("0.0", CultureInfo.InvariantCulture)}GB"
            : $"{((int)Math.Round(gigabytes)).ToInvariantString()}GB";
    }

    public static string HumanSize(this int bytes) => ((long)bytes).HumanSize();

    public static string FormatFactor(this double? factor)
    {
        if (factor is null)
        {
            return string.Empty;
        }

        var value = factor.Value;
        if (Math.Abs(value * 10 % 1) < 0.001)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }
        if (Math.Abs(value * 100 % 1) < 0.001)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }
        return value.ToString("0.000", CultureInfo.InvariantCulture);
    }

    public static string FormatCropSize(this CropSize? cropSize)
    {
        if (cropSize is null)
        {
            return string.Empty;
        }

        var size = cropSize.Value;
        if (size.LongEdge)
        {
            return size.Width == 0 ? size.Height.ToInvariantString() : size.Width.ToInvariantString();
        }
        if (size.Width == 0)
        {
            return size.Height.ToInvariantString();
        }
        if (size.Height == 0)
        {
            return size.Width.ToInvariantString();
        }
        return $"{size.Width.ToInvariantString()}x{size.Height.ToInvariantString()}";
    }

    public static string Format(this double value, int decimals) => value.ToString($"F{decimals}", CultureInfo.InvariantCulture);

    public static int OrElseNonZero(this int? value, int fallback) => value is null or 0 ? fallback : value.Value;

    public static long OrElseNonZero(this long? value, long fallback) => value is null or 0 ? fallback : value.Value;
}
