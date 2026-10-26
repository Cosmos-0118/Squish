using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Processes;
using Squish.Core.Shared;
using ImageMagick;

namespace Squish.Core.Imaging;

internal enum ChromaSubsampling
{
    /// <summary>4:2:0 - smallest, right for photographs.</summary>
    Subsampled420,

    /// <summary>4:4:4 - keeps colour edges crisp for UI, text and line art.</summary>
    Full444
}

/// <summary>
/// Thin wrappers around the external encoders (mozjpeg, oxipng, pngquant, cwebp, avifenc, gifsicle) with
/// Magick.NET based fallbacks so the pipeline still works - just less aggressively - when a tool is missing.
/// Every method writes a new temp file and returns its path, or null when the encoder failed or produced
/// nothing. Callers own the returned files (see <see cref="TempFileScope"/>).
/// </summary>
internal sealed class ImageEncoders
{
    private static readonly ProcessRunnerOptions Quiet = ProcessRunnerOptions.Create(throwOnError: false);
    private readonly ImageToolset _tools;
    private readonly TempFileScope _temp;

    public ImageEncoders(ImageToolset tools, TempFileScope temp)
    {
        _tools = tools;
        _temp = temp;
    }

    public ImageToolset Tools => _tools;

    // ---- JPEG --------------------------------------------------------------------------------------------

    public bool HasMozJpeg => _tools.Cjpeg is not null;

    public bool HasJpegli => _tools.Cjpegli is not null;

    public bool HasLosslessJpeg => _tools.Jpegtran is not null;

    public async Task<FilePath?> EncodeJpegAsync(RasterImage raster, int quality, ChromaSubsampling chroma, CancellationToken token)
    {
        quality = Math.Clamp(quality, 1, 100);
        var output = _temp.New("squish-jpg", "jpg");

        if (_tools.Cjpeg is { } cjpeg)
        {
            var staging = _temp.New("squish-stage", "ppm");
            WritePpm(raster, staging.Value);

            var args = new List<string>
            {
                "-quality", quality.ToString(CultureInfo.InvariantCulture),
                "-progressive",
                "-sample", chroma == ChromaSubsampling.Full444 ? "1x1" : "2x2",
                "-outfile", output.Value,
                staging.Value
            };

            var result = await ProcessRunner.RunAsync(cjpeg, args, Quiet, token).ConfigureAwait(false);
            _temp.Release(staging);
            return Succeeded(result, output) ? output : null;
        }

        return await Task.Run(() =>
        {
            ImageIO.EnsureConfigured();
            var settings = new PixelReadSettings((uint)raster.Width, (uint)raster.Height, StorageType.Char, PixelMapping.RGBA);
            using var image = new MagickImage();
            image.ReadPixels(raster.Pixels, settings);
            image.Alpha(AlphaAction.Off);
            image.Quality = (uint)quality;
            image.Settings.Interlace = Interlace.Plane;
            image.Settings.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", chroma == ChromaSubsampling.Full444 ? "1x1" : "2x2");
            image.Format = MagickFormat.Jpeg;
            image.Write(output.Value);
            return (FilePath?)output;
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    /// jpegli at a given quality. Adaptive quantisation is off: it spends bits where a human eye is least sensitive,
    /// which SSIMULACRA2 does not reward, and it measured 5-25% smaller at the same score on photographs.
    /// </summary>
    public async Task<FilePath?> EncodeJpegliAsync(RasterImage raster, int quality, ChromaSubsampling chroma, CancellationToken token)
    {
        if (_tools.Cjpegli is not { } cjpegli)
        {
            return null;
        }

        var output = _temp.New("squish-jpegli", "jpg");
        var staging = _temp.New("squish-stage", "ppm");
        WritePpm(raster, staging.Value);

        var args = new List<string>
        {
            "-q", Math.Clamp(quality, 1, 100).ToString(CultureInfo.InvariantCulture),
            "-p", "2",
            chroma == ChromaSubsampling.Full444 ? "--chroma_subsampling=444" : "--chroma_subsampling=420",
            "--noadaptive_quantization",
            staging.Value,
            output.Value
        };

        var result = await ProcessRunner.RunAsync(cjpegli, args, Quiet, token).ConfigureAwait(false);
        _temp.Release(staging);
        return Succeeded(result, output) ? output : null;
    }

    /// <summary>Lossless Huffman re-optimisation + progressive scan script. Coefficients are untouched, so pixels stay identical.</summary>
    public async Task<FilePath?> RecompressJpegLosslessAsync(FilePath source, CancellationToken token)
    {
        if (_tools.Jpegtran is not { } jpegtran)
        {
            return null;
        }

        var output = _temp.New("squish-jpgl", "jpg");
        var args = new[] { "-optimize", "-progressive", "-copy", "none", "-outfile", output.Value, source.Value };
        var result = await ProcessRunner.RunAsync(jpegtran, args, Quiet, token).ConfigureAwait(false);
        return Succeeded(result, output) ? output : null;
    }

    // ---- PNG ---------------------------------------------------------------------------------------------

    public bool HasOxipng => _tools.Oxipng is not null;

    public bool HasPngquant => _tools.Pngquant is not null;

    /// <param name="level">oxipng preset 0-6 (or max).</param>
    /// <param name="zopfli">Use the much slower Zopfli deflater for the last few percent.</param>
    /// <param name="strip">oxipng --strip mode: none, safe or all.</param>
    public async Task<FilePath?> OptimisePngLosslessAsync(FilePath source, int level, bool zopfli, string strip, CancellationToken token)
    {
        var output = _temp.New("squish-png", "png");

        if (_tools.Oxipng is { } oxipng)
        {
            var args = new List<string>
            {
                "--opt", Math.Clamp(level, 0, 6).ToString(CultureInfo.InvariantCulture),
                "--interlace", "0",
                "--quiet"
            };

            if (!string.Equals(strip, "none", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--strip");
                args.Add(strip);
            }

            if (zopfli)
            {
                args.Add("--zopfli");
            }

            args.Add("--out");
            args.Add(output.Value);
            args.Add(source.Value);

            var result = await ProcessRunner.RunAsync(oxipng, args, Quiet, token).ConfigureAwait(false);
            return Succeeded(result, output) ? output : null;
        }

        return await Task.Run(() =>
        {
            ImageIO.EnsureConfigured();
            using var image = new MagickImage(source.Value, new MagickReadSettings { Format = MagickFormat.Png });
            if (string.Equals(strip, "all", StringComparison.OrdinalIgnoreCase))
            {
                image.Strip();
            }

            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            image.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
            image.Settings.SetDefine(MagickFormat.Png, "compression-strategy", "1");
            image.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", strip == "none" ? "date" : "all,iCCP");
            image.Settings.Interlace = Interlace.NoInterlace;
            image.Format = MagickFormat.Png;
            image.Write(output.Value);
            return (FilePath?)output;
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Lossy palette quantisation (pngquant). Returns null when pngquant cannot reach <paramref name="minQuality"/>
    /// (exit code 99), which is exactly the signal we want: "this image does not survive quantisation".
    /// </summary>
    public async Task<FilePath?> QuantisePngAsync(FilePath source, int minQuality, int maxQuality, int speed, CancellationToken token)
    {
        if (_tools.Pngquant is not { } pngquant)
        {
            return null;
        }

        var output = _temp.New("squish-pq", "png");
        var args = new[]
        {
            "--quality", $"{Math.Clamp(minQuality, 0, 100)}-{Math.Clamp(maxQuality, 0, 100)}",
            "--speed", Math.Clamp(speed, 1, 11).ToString(CultureInfo.InvariantCulture),
            "--strip", "--force", "--skip-if-larger",
            "--output", output.Value,
            source.Value
        };

        var result = await ProcessRunner.RunAsync(pngquant, args, Quiet, token).ConfigureAwait(false);
        return Succeeded(result, output) ? output : null;
    }

    public FilePath WritePngStaging(RasterImage raster, bool fast = true)
    {
        var path = _temp.New("squish-stage", "png");
        ImageIO.WriteRasterAsPng(raster, path.Value, fast);
        return path;
    }

    // ---- WebP / AVIF -------------------------------------------------------------------------------------

    public bool HasWebp => _tools.Cwebp is not null;

    public bool HasAvif => _tools.Avifenc is not null;

    public async Task<FilePath?> EncodeWebpAsync(FilePath stagingPng, int quality, bool lossless, CancellationToken token)
    {
        if (_tools.Cwebp is not { } cwebp)
        {
            return null;
        }

        var output = _temp.New("squish-webp", "webp");
        var args = new List<string> { "-quiet", "-mt", "-m", "6", "-metadata", "none" };
        if (lossless)
        {
            args.AddRange(new[] { "-lossless", "-z", "9", "-exact" });
        }
        else
        {
            args.AddRange(new[]
            {
                "-q", Math.Clamp(quality, 0, 100).ToString(CultureInfo.InvariantCulture),
                "-sharp_yuv", "-af", "-alpha_q", "100"
            });
        }

        args.AddRange(new[] { stagingPng.Value, "-o", output.Value });
        var result = await ProcessRunner.RunAsync(cwebp, args, Quiet, token).ConfigureAwait(false);
        return Succeeded(result, output) ? output : null;
    }

    public async Task<FilePath?> EncodeAvifAsync(FilePath stagingPng, int quality, ChromaSubsampling chroma, int speed, CancellationToken token)
    {
        if (_tools.Avifenc is not { } avifenc)
        {
            return null;
        }

        var output = _temp.New("squish-avif", "avif");
        var args = new List<string>
        {
            "--qcolor", Math.Clamp(quality, 0, 100).ToString(CultureInfo.InvariantCulture),
            "--qalpha", "90",
            "--speed", Math.Clamp(speed, 0, 10).ToString(CultureInfo.InvariantCulture),
            "--jobs", "all",
            "--depth", "8",
            "--yuv", chroma == ChromaSubsampling.Full444 ? "444" : "420",
            "--ignore-exif", "--ignore-xmp",
            stagingPng.Value,
            output.Value
        };

        var result = await ProcessRunner.RunAsync(avifenc, args, Quiet, token).ConfigureAwait(false);
        return Succeeded(result, output) ? output : null;
    }

    // ---- GIF ---------------------------------------------------------------------------------------------

    public bool HasGifsicle => _tools.Gifsicle is not null;

    public async Task<FilePath?> OptimiseGifAsync(FilePath source, int lossy, CancellationToken token)
    {
        var output = _temp.New("squish-gif", "gif");

        if (_tools.Gifsicle is { } gifsicle)
        {
            var args = new List<string> { "-O3", "--no-comments", "--no-names", "--no-extensions" };
            if (lossy > 0)
            {
                args.Add($"--lossy={Math.Clamp(lossy, 1, 200)}");
            }

            args.AddRange(new[] { source.Value, "-o", output.Value });
            var result = await ProcessRunner.RunAsync(gifsicle, args, Quiet, token).ConfigureAwait(false);
            return Succeeded(result, output) ? output : null;
        }

        return await Task.Run(() =>
        {
            ImageIO.EnsureConfigured();
            using var stream = new FileStream(source.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var frames = new MagickImageCollection();
            frames.Read(stream, new MagickReadSettings { Format = MagickFormat.Gif });
            if (frames.Count > 1)
            {
                frames.Coalesce();
                frames.OptimizePlus();
                frames.OptimizeTransparency();
            }

            frames.Write(output.Value, MagickFormat.Gif);
            return (FilePath?)output;
        }, token).ConfigureAwait(false);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static bool Succeeded(ProcessResult result, FilePath output)
    {
        if (result.ExitCode != 0)
        {
            return false;
        }

        try
        {
            return File.Exists(output.Value) && new FileInfo(output.Value).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Binary PPM (P6) staging file - the cheapest format every JPEG encoder understands.</summary>
    public static void WritePpm(RasterImage raster, string destination)
    {
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{raster.Width} {raster.Height}\n255\n");
        stream.Write(header);

        var rowBuffer = new byte[raster.Width * 3];
        var pixels = raster.Pixels;
        for (var y = 0; y < raster.Height; y++)
        {
            var source = y * raster.Width * 4;
            for (int x = 0, d = 0; x < raster.Width; x++, d += 3)
            {
                var i = source + x * 4;
                var alpha = pixels[i + 3];
                if (alpha == byte.MaxValue)
                {
                    rowBuffer[d] = pixels[i];
                    rowBuffer[d + 1] = pixels[i + 1];
                    rowBuffer[d + 2] = pixels[i + 2];
                }
                else
                {
                    // Flatten onto white: JPEG has no alpha, and white is what viewers show behind transparent PNGs.
                    rowBuffer[d] = (byte)((pixels[i] * alpha + 255 * (255 - alpha) + 127) / 255);
                    rowBuffer[d + 1] = (byte)((pixels[i + 1] * alpha + 255 * (255 - alpha) + 127) / 255);
                    rowBuffer[d + 2] = (byte)((pixels[i + 2] * alpha + 255 * (255 - alpha) + 127) / 255);
                }
            }

            stream.Write(rowBuffer);
        }
    }
}
