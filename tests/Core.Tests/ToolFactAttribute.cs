using System;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>Resolves the optional external encoders exactly like the product does.</summary>
internal static class ToolAvailability
{
    public static string? Cjpeg => ExternalToolResolver.Resolve("cjpeg-static", "mozjpeg", "CLOP_CJPEG", null, "mozjpeg", "cjpeg");

    public static string? Jpegtran => ExternalToolResolver.Resolve("jpegtran-static", "mozjpeg", "CLOP_JPEGTRAN", null, "jpegtran");

    public static string? Oxipng => ExternalToolResolver.Resolve("oxipng", "oxipng", "CLOP_OXIPNG");

    public static string? Pngquant => ExternalToolResolver.Resolve("pngquant", "pngquant", "CLOP_PNGQUANT");

    public static string? Cwebp => ExternalToolResolver.Resolve("cwebp", "libwebp", "CLOP_CWEBP");

    public static string? Avifenc => ExternalToolResolver.Resolve("avifenc", "libavif", "CLOP_AVIFENC");

    public static string? Gifsicle => ExternalToolResolver.Resolve("gifsicle", "gifsicle", "CLOP_GIFSICLE");

    public static string? Ffmpeg
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("CLOP_REAL_FFMPEG");
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
            var fromEnv = Environment.GetEnvironmentVariable("CLOP_REAL_FFPROBE");
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
                Skip = $"Requires external tool '{tool}' (set CLOP_TOOLS_DIR or the CLOP_* path variables).";
                return;
            }
        }
    }
}
