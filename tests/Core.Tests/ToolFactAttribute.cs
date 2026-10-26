using System;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>Resolves the optional external encoders exactly like the product does.</summary>
internal static class ToolAvailability
{
    public static string? Cjpeg => ExternalToolResolver.Resolve("cjpeg-static", "mozjpeg", "SQUISH_CJPEG", null, "mozjpeg", "cjpeg");

    public static string? Jpegtran => ExternalToolResolver.Resolve("jpegtran-static", "mozjpeg", "SQUISH_JPEGTRAN", null, "jpegtran");

    public static string? Oxipng => ExternalToolResolver.Resolve("oxipng", "oxipng", "SQUISH_OXIPNG");

    public static string? Pngquant => ExternalToolResolver.Resolve("pngquant", "pngquant", "SQUISH_PNGQUANT");

    public static string? Cwebp => ExternalToolResolver.Resolve("cwebp", "libwebp", "SQUISH_CWEBP");

    public static string? Avifenc => ExternalToolResolver.Resolve("avifenc", "libavif", "SQUISH_AVIFENC");

    public static string? Gifsicle => ExternalToolResolver.Resolve("gifsicle", "gifsicle", "SQUISH_GIFSICLE");

    public static string? Ffmpeg
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("SQUISH_REAL_FFMPEG");
            if (!string.IsNullOrWhiteSpace(fromEnv) && System.IO.File.Exists(fromEnv))
            {
                return fromEnv;
            }

            return ExternalToolResolver.Resolve("ffmpeg", "ffmpeg", null);
        }
    }

    public static string? Ffprobe
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("SQUISH_REAL_FFPROBE");
            if (!string.IsNullOrWhiteSpace(fromEnv) && System.IO.File.Exists(fromEnv))
            {
                return fromEnv;
            }

            return ExternalToolResolver.Resolve("ffprobe", "ffmpeg", null);
        }
    }

    public static string? Find(string tool) => tool switch
    {
        "mozjpeg" => Cjpeg,
        "jpegtran" => Jpegtran,
        "oxipng" => Oxipng,
        "pngquant" => Pngquant,
        "cwebp" => Cwebp,
        "avifenc" => Avifenc,
        "gifsicle" => Gifsicle,
        "ffmpeg" => Ffmpeg,
        "ffprobe" => Ffprobe,
        _ => null
    };
}

/// <summary>A fact that is skipped (visibly) when the named external tool is not installed on the machine running the tests.</summary>
public sealed class ToolFactAttribute : FactAttribute
{
    public ToolFactAttribute(params string[] tools)
    {
        foreach (var tool in tools)
        {
            if (ToolAvailability.Find(tool) is null)
            {
                Skip = $"Requires external tool '{tool}' (set SQUISH_TOOLS_DIR or the SQUISH_* path variables).";
                return;
            }
        }
    }
}
