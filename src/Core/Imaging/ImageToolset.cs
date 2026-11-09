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

    public string? Cjpegli { get; init; }

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
            Cjpegli = preferences.EnableJpegli
                ? ExternalToolResolver.Resolve("cjpegli", "jpegli", "SQUISH_CJPEGLI", preferences.JpegliPath)
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

/// <summary>
/// Tracks temp files created during one optimisation so every exit path cleans up after itself. Thread-safe: a job
/// runs independent encoder searches concurrently. Files are made in a directory of the scope's own
/// (<see cref="TempRuns"/>), created on first use and deleted whole on dispose - so even a file nobody released
/// cannot outlive the job, and a crashed process leaves one identifiable directory for the next start-up sweep.
/// </summary>
internal sealed class TempFileScope : IDisposable
{
    private readonly List<string> _files = new();
    private readonly object _gate = new();
    private string? _directory;

    public FilePath New(string prefix, string extension)
    {
        lock (_gate)
        {
            _directory ??= TempRuns.CreateDirectory();
            var ext = string.IsNullOrEmpty(extension) ? ".tmp" : extension.StartsWith('.') ? extension : "." + extension;
            var path = new FilePath(Path.Combine(_directory, $"{prefix}-{Guid.NewGuid():N}{ext}"));
            _files.Add(path.Value);
            return path;
        }
    }

    public FilePath Track(FilePath path)
    {
        lock (_gate)
        {
            _files.Add(path.Value);
        }

        return path;
    }

    public void Release(FilePath path)
    {
        Delete(path.Value);
        lock (_gate)
        {
            _files.Remove(path.Value);
        }
    }

    public void Dispose()
    {
        string[] files;
        string? directory;
        lock (_gate)
        {
            files = _files.ToArray();
            _files.Clear();
            directory = _directory;
            _directory = null;
        }

        foreach (var file in files)
        {
            Delete(file);
        }

        if (directory is not null)
        {
            TempRuns.TryDelete(directory);
        }
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
