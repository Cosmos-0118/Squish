using System;
using System.Collections.Generic;
using System.IO;
using Squish.Core.Optimizers;
using Squish.Core.Shared;

namespace Squish.Core.Imaging;

/// <summary>Resolved external encoders. Every member is optional: missing tools simply disable the matching strategy.</summary>
internal sealed class ImageToolset
{
    public string? Cjpeg { get; init; }

    public string? Jpegtran { get; init; }

    public string? Oxipng { get; init; }

    public string? Pngquant { get; init; }

    public string? Cwebp { get; init; }

    public string? Avifenc { get; init; }

    public string? Gifsicle { get; init; }

    public static ImageToolset Resolve(AdvancedCodecPreferences preferences)
    {
        preferences ??= AdvancedCodecPreferences.Disabled;

        return new ImageToolset
        {
            Cjpeg = preferences.EnableMozJpeg
                ? ExternalToolResolver.Resolve("cjpeg-static", "mozjpeg", "SQUISH_CJPEG", preferences.MozJpegPath, "mozjpeg", "cjpeg")
                : null,
            Jpegtran = preferences.EnableMozJpeg
                ? ExternalToolResolver.Resolve("jpegtran-static", "mozjpeg", "SQUISH_JPEGTRAN", preferences.JpegtranPath, "jpegtran")
                : null,
            Oxipng = preferences.EnableOxipng
                ? ExternalToolResolver.Resolve("oxipng", "oxipng", "SQUISH_OXIPNG", preferences.OxipngPath)
                : null,
            Pngquant = preferences.EnablePngquant
                ? ExternalToolResolver.Resolve("pngquant", "pngquant", "SQUISH_PNGQUANT", preferences.PngquantPath)
                : null,
            Cwebp = preferences.EnableWebp
                ? ExternalToolResolver.Resolve("cwebp", "libwebp", "SQUISH_CWEBP", preferences.CwebpPath)
                : null,
            Avifenc = preferences.EnableAvif
                ? ExternalToolResolver.Resolve("avifenc", "libavif", "SQUISH_AVIFENC", preferences.AvifEncPath)
                : null,
            Gifsicle = preferences.EnableGifsicle
                ? ExternalToolResolver.Resolve("gifsicle", "gifsicle", "SQUISH_GIFSICLE", preferences.GifsiclePath)
                : null
        };
    }
}

/// <summary>Tracks temp files created during one optimisation so every exit path cleans up after itself.</summary>
internal sealed class TempFileScope : IDisposable
{
    private readonly List<string> _files = new();

    public FilePath New(string prefix, string extension)
    {
        var path = FilePath.TempFile(prefix, extension, addUniqueSuffix: true);
        _files.Add(path.Value);
        return path;
    }

    public FilePath Track(FilePath path)
    {
        _files.Add(path.Value);
        return path;
    }

    public void Release(FilePath path)
    {
        Delete(path.Value);
        _files.Remove(path.Value);
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            Delete(file);
        }

        _files.Clear();
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
