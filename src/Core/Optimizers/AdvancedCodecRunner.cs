using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Processes;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

internal sealed class AdvancedCodecRunner
{
    private readonly AdvancedCodecPreferences _preferences;

    public AdvancedCodecRunner(AdvancedCodecPreferences preferences)
    {
        _preferences = preferences ?? AdvancedCodecPreferences.Disabled;
    }

    public bool HasAnyCodec => _preferences.EnableMozJpeg || _preferences.EnableWebp || _preferences.EnableAvif || _preferences.EnableHeifConvert;

    public async Task<AdvancedCodecResult?> TryEncodeAsync(
        FilePath stagedInput,
        ImageSaveProfile saveProfile,
        ImageOptimiserOptions options,
        ImageContentProfile profile,
        CancellationToken cancellationToken)
    {
        if (!HasAnyCodec || !File.Exists(stagedInput.Value))
        {
            return null;
        }

        var attempts = BuildCandidateOrder(profile, saveProfile);
        foreach (var codec in attempts)
        {
            var result = await codec(stagedInput, saveProfile, options, profile, cancellationToken).ConfigureAwait(false);
            if (result is { Succeeded: true })
            {
                return result;
            }
        }

        return null;
    }

    private IReadOnlyList<Func<FilePath, ImageSaveProfile, ImageOptimiserOptions, ImageContentProfile, CancellationToken, Task<AdvancedCodecResult?>>> BuildCandidateOrder(ImageContentProfile profile, ImageSaveProfile saveProfile)
    {
        var list = new List<Func<FilePath, ImageSaveProfile, ImageOptimiserOptions, ImageContentProfile, CancellationToken, Task<AdvancedCodecResult?>>>(capacity: 3);

        if (profile.Kind == ImageContentKind.Document)
        {
            if (_preferences.EnableWebp)
            {
                list.Add(EncodeWithWebpAsync);
            }
            return list;
        }

        if (profile.IsPhotographic)
        {
            if (_preferences.EnableAvif && _preferences.PreferAvifForPhotographic)
            {
                list.Add(EncodeWithAvifAsync);
            }

            if (_preferences.EnableWebp && _preferences.PreferWebpFallback)
            {
                list.Add(EncodeWithWebpAsync);
            }

            if (_preferences.EnableMozJpeg && string.Equals(saveProfile.Extension, "jpg", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(EncodeWithMozJpegAsync);
            }

            return list;
        }

        if (_preferences.EnableWebp)
        {
            list.Add(EncodeWithWebpAsync);
        }

        if (_preferences.EnableMozJpeg && string.Equals(saveProfile.Extension, "jpg", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(EncodeWithMozJpegAsync);
        }

        if (_preferences.EnableAvif && !_preferences.PreferAvifForPhotographic)
        {
            list.Add(EncodeWithAvifAsync);
        }

        return list;
    }

    private async Task<AdvancedCodecResult?> EncodeWithMozJpegAsync(FilePath input, ImageSaveProfile saveProfile, ImageOptimiserOptions options, ImageContentProfile profile, CancellationToken token)
    {
        if (!string.Equals(saveProfile.Extension, "jpg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var executable = ResolveExecutable(_preferences.MozJpegPath, new[] { "mozjpeg.exe", "cjpeg.exe", "cjpeg-static.exe" });
        if (executable is null)
        {
            return null;
        }

        var quality = Math.Clamp(options.TargetJpegQuality, 30, 95);
        var output = FilePath.TempFile("clop-image", "jpg", addUniqueSuffix: true);

        var arguments = new List<string>
        {
            "-quality", quality.ToString(),
            "-progressive",
            "-optimize",
            "-sample", "444",
            input.Value,
            output.Value
        };

        var result = await ProcessRunner.RunAsync(executable, arguments, ProcessRunnerOptions.Create(throwOnError: false), token).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output.Value))
        {
            TryDelete(output);
            return null;
        }

        return AdvancedCodecResult.Success(output, "mozjpeg", "Converted to mozjpeg");
    }

    private async Task<AdvancedCodecResult?> EncodeWithWebpAsync(FilePath input, ImageSaveProfile saveProfile, ImageOptimiserOptions options, ImageContentProfile profile, CancellationToken token)
    {
        var executable = ResolveExecutable(_preferences.CwebpPath, new[] { "cwebp.exe" });
        if (executable is null)
        {
            return null;
        }

        var quality = Math.Clamp(options.TargetJpegQuality, 40, 95);
        var output = FilePath.TempFile("clop-image", "webp", addUniqueSuffix: true);

        var arguments = new List<string>
        {
            "-q", quality.ToString(),
            "-mt",
            "-metadata", options.MetadataPolicy.PreserveMetadata ? "all" : "none",
            input.Value,
            "-o", output.Value
        };

        var result = await ProcessRunner.RunAsync(executable, arguments, ProcessRunnerOptions.Create(throwOnError: false), token).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output.Value))
        {
            TryDelete(output);
            return null;
        }

        return AdvancedCodecResult.Success(output, "webp", "Converted to WebP");
    }

    private async Task<AdvancedCodecResult?> EncodeWithAvifAsync(FilePath input, ImageSaveProfile saveProfile, ImageOptimiserOptions options, ImageContentProfile profile, CancellationToken token)
    {
        var executable = ResolveExecutable(_preferences.AvifEncPath, new[] { "avifenc.exe" });
        if (executable is null)
        {
            return null;
        }

        var quality = Math.Clamp(options.TargetJpegQuality, 35, 95);
        var output = FilePath.TempFile("clop-image", "avif", addUniqueSuffix: true);

        var arguments = new List<string>
        {
            "--min", (Math.Max(0, quality - 10)).ToString(),
            "--max", quality.ToString(),
            "--speed", profile.IsPhotographic ? "4" : "6",
            "--yuv", profile.IsPhotographic ? "444" : "420",
            input.Value,
            output.Value
        };

        var result = await ProcessRunner.RunAsync(executable, arguments, ProcessRunnerOptions.Create(throwOnError: false), token).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output.Value))
        {
            TryDelete(output);
            return null;
        }

        return AdvancedCodecResult.Success(output, "avif", "Converted to AVIF");
    }

    private static string? ResolveExecutable(string? preferredPath, IReadOnlyList<string> fallbacks)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            return preferredPath;
        }

        foreach (var candidate in fallbacks)
        {
            var path = ResolveFromPath(candidate);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    private static string? ResolveFromPath(string command)
    {
        var environmentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in environmentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var probe = Path.Combine(segment, command);
            if (File.Exists(probe))
            {
                return probe;
            }
        }

        return null;
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
}

internal sealed record AdvancedCodecResult(FilePath OutputPath, string Format, string Message, bool Succeeded)
{
    public static AdvancedCodecResult Success(FilePath outputPath, string format, string message) => new(outputPath, format, message, true);
}
