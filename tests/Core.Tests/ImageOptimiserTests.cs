using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Core.Tests;

[SupportedOSPlatform("windows6.1")]
public sealed class ImageOptimiserTests
{
    [Fact]
    public async Task ConvertsPngToJpegWhenEligible()
    {
        var source = CreateSampleImage("png", width: 1600, height: 900, includeAlpha: false, includeMetadata: true);
        var outputs = new List<FilePath>();
        try
        {
            var optimiser = new ImageOptimiser(new ImageOptimiserOptions { TargetJpegQuality = 60 });
            var result = await RunOptimiserAsync(optimiser, source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var output = EnsureOutputPath(result);
            outputs.Add(output);
            Assert.EndsWith(".jpg", output.Value, StringComparison.OrdinalIgnoreCase);

            var originalSize = new FileInfo(source.Value).Length;
            var newSize = new FileInfo(output.Value).Length;
            Assert.True(newSize < originalSize, $"Expected JPEG ({newSize}) to be smaller than PNG ({originalSize})");
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task RejectsImagesExceedingPixelLimit()
    {
        var source = CreateSampleImage("jpg", width: 200, height: 200, includeAlpha: false, includeMetadata: false);
        var outputs = new List<FilePath>();
        try
        {
            var options = new ImageOptimiserOptions { MaxInputPixels = 10_000, MaxDimension = null };
            var optimiser = new ImageOptimiser(options);

            var result = await RunOptimiserAsync(optimiser, source);

            Assert.Equal(OptimisationStatus.Failed, result.Status);
            Assert.Contains("pixel limit", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task RejectsImagesExceedingDimensionLimit()
    {
        var source = CreateSampleImage("jpg", width: 200, height: 80, includeAlpha: false, includeMetadata: false);
        var outputs = new List<FilePath>();
        try
        {
            var options = new ImageOptimiserOptions { MaxDimension = 100, MaxInputPixels = 1_000_000 };
            var optimiser = new ImageOptimiser(options);

            var result = await RunOptimiserAsync(optimiser, source);

            Assert.Equal(OptimisationStatus.Failed, result.Status);
            Assert.Contains("dimension limit", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task DownscalesRetinaImages()
    {
        var source = CreateSampleImage("jpg", width: 5200, height: 3400, includeAlpha: false, includeMetadata: false);
        var outputs = new List<FilePath>();
        try
        {
            var options = new ImageOptimiserOptions
            {
                TargetJpegQuality = 75,
                DownscaleRetina = true,
                RetinaLongEdgePixels = 2048
            };
            var optimiser = new ImageOptimiser(options);
            var result = await RunOptimiserAsync(optimiser, source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var output = EnsureOutputPath(result);
            outputs.Add(output);

            using var image = Image.Load<Rgba32>(output.Value);
            Assert.True(Math.Max(image.Width, image.Height) <= options.RetinaLongEdgePixels,
                $"Expected long edge <= {options.RetinaLongEdgePixels}, got {image.Width}x{image.Height}");
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task PreservesMetadataWhenRequested()
    {
        var source = CreateSampleImage("jpg", width: 800, height: 600, includeAlpha: false, includeMetadata: true);
        var outputs = new List<FilePath>();
        try
        {
            var options = new ImageOptimiserOptions { PreserveMetadata = true };
            var optimiser = new ImageOptimiser(options);
            var result = await RunOptimiserAsync(optimiser, source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var output = EnsureOutputPath(result);
            outputs.Add(output);

            using var image = Image.Load<Rgba32>(output.Value);
            var exif = image.Metadata.ExifProfile;
            Assert.NotNull(exif);
            var nonNullExif = exif!;
            Assert.True(nonNullExif.TryGetValue(ExifTag.ImageDescription, out var description));
            var descriptionValue = description?.Value?.ToString();
            Assert.Equal("Clop Windows Test", descriptionValue);
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task FastPathRemovesMetadataWhenSavingsThresholdMet()
    {
        var source = CreateSampleImage("jpg", width: 1024, height: 768, includeAlpha: false, includeMetadata: true);
        var outputs = new List<FilePath>();
        try
        {
            var options = new ImageOptimiserOptions
            {
                DownscaleRetina = false,
                AdvancedCodecs = AdvancedCodecPreferences.Disabled,
                CropSuggestions = CropSuggestionOptions.Disabled,
                WicFastPath = new WicFastPathOptions
                {
                    Enabled = true,
                    StripMetadata = true,
                    MinimumSavingsPercent = 0,
                    SkipLosslessWhenBelowThreshold = false,
                    OverrideJpegQuality = 80
                },
                MetadataPolicy = MetadataPolicyOptions.Default with { PreserveMetadata = false, PreserveColorProfiles = false, StripGpsMetadata = false }
            };

            var optimiser = new ImageOptimiser(options);
            var result = await RunOptimiserAsync(optimiser, source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Contains("fast path", result.Message, StringComparison.OrdinalIgnoreCase);

            var output = EnsureOutputPath(result);
            outputs.Add(output);

            var originalSize = new FileInfo(source.Value).Length;
            var newSize = new FileInfo(output.Value).Length;
            Assert.True(newSize < originalSize, "Fast path should reduce file size when metadata is stripped.");

            using var optimisedImage = Image.Load<Rgba32>(output.Value);
            Assert.Null(optimisedImage.Metadata.ExifProfile);
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task FastPathSkipsWhenSavingsBelowThreshold()
    {
        var source = CreateSampleImage("png", width: 256, height: 256, includeAlpha: false, includeMetadata: false);
        var outputs = new List<FilePath>();
        try
        {
            var options = new ImageOptimiserOptions
            {
                DownscaleRetina = false,
                AdvancedCodecs = AdvancedCodecPreferences.Disabled,
                CropSuggestions = CropSuggestionOptions.Disabled,
                WicFastPath = new WicFastPathOptions
                {
                    Enabled = true,
                    MinimumSavingsPercent = 50,
                    SkipLosslessWhenBelowThreshold = true
                }
            };

            var optimiser = new ImageOptimiser(options);
            var result = await RunOptimiserAsync(optimiser, source);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Equal(source.Value, result.OutputPath?.Value);
            Assert.Equal("Original already optimal (fast path)", result.Message);
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task ReplacesOriginalWhenMetadataRequestsInPlace()
    {
        var source = CreateSampleImage("jpg", width: 1200, height: 800, includeAlpha: false, includeMetadata: false);
        var outputs = new List<FilePath>();
        try
        {
            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [OptimisationMetadata.OutputReplaceOriginal] = true
            };

            var optimiser = CreateMetadataOptimiser();
            var result = await RunOptimiserAsync(optimiser, source, metadata);

            Assert.True(result.Status == OptimisationStatus.Succeeded, $"Optimisation failed: {result.Message}");
            Assert.Equal(source.Value, result.OutputPath?.Value);

            var clopCopy = source.Parent.Append($"{source.Stem}.clop.jpg");
            Assert.False(File.Exists(clopCopy.Value));

        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task ConversionKeepsOriginalWhenDeleteFlagNotSet()
    {
        var source = CreateSampleImage("bmp", width: 1024, height: 768, includeAlpha: false, includeMetadata: false);
        var outputs = new List<FilePath>();
        try
        {
            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [OptimisationMetadata.OutputReplaceOriginal] = true
            };

            var optimiser = CreateMetadataOptimiser();
            var result = await RunOptimiserAsync(optimiser, source, metadata);
            Assert.True(result.Status == OptimisationStatus.Succeeded, $"Optimisation failed: {result.Message}");

            var expectedOutput = source.Parent.Append($"{source.Stem}.jpg");
            outputs.Add(expectedOutput);
            Assert.Equal(expectedOutput.Value, result.OutputPath?.Value);
            Assert.True(File.Exists(source.Value));
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    [Fact]
    public async Task ConversionDeletesOriginalWhenDeleteFlagSet()
    {
        var source = CreateSampleImage("bmp", width: 1024, height: 768, includeAlpha: false, includeMetadata: false);
        var outputs = new List<FilePath>();
        try
        {
            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [OptimisationMetadata.OutputReplaceOriginal] = true,
                [OptimisationMetadata.OutputDeleteConvertedSource] = true
            };

            var optimiser = CreateMetadataOptimiser();
            var result = await RunOptimiserAsync(optimiser, source, metadata);
            Assert.True(result.Status == OptimisationStatus.Succeeded, $"Optimisation failed: {result.Message}");

            var expectedOutput = source.Parent.Append($"{source.Stem}.jpg");
            outputs.Add(expectedOutput);
            Assert.Equal(expectedOutput.Value, result.OutputPath?.Value);
            Assert.False(File.Exists(source.Value));
        }
        finally
        {
            CleanupFiles(source, outputs);
        }
    }

    private static async Task<OptimisationResult> RunOptimiserAsync(ImageOptimiser optimiser, FilePath source, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Image, source, metadata: metadata));
        return await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static ImageOptimiser CreateMetadataOptimiser()
    {
        var options = new ImageOptimiserOptions
        {
            TargetJpegQuality = 70,
            RequireSizeImprovement = false,
            DownscaleRetina = false,
            AdvancedCodecs = AdvancedCodecPreferences.Disabled,
            CropSuggestions = CropSuggestionOptions.Disabled,
            WicFastPath = new WicFastPathOptions { Enabled = false }
        };

        return new ImageOptimiser(options);
    }

    private static FilePath EnsureOutputPath(OptimisationResult result)
    {
        Assert.NotNull(result.OutputPath);
        return result.OutputPath!.Value;
    }

    private static FilePath CreateSampleImage(string extension, int width, int height, bool includeAlpha, bool includeMetadata)
    {
        var path = FilePath.TempFile("image-optimiser-test", extension, addUniqueSuffix: true);
        using var image = new Image<Rgba32>(width, height);

        var random = new Random(42);
        for (var y = 0; y < height; y++)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < width; x++)
            {
                var r = (byte)random.Next(32, 240);
                var g = (byte)random.Next(32, 240);
                var b = (byte)random.Next(32, 240);
                var color = includeAlpha
                    ? new Rgba32(r, g, b, 220)
                    : new Rgba32(r, g, b);
                row[x] = color;
            }
        }

        if (includeMetadata)
        {
            var exif = image.Metadata.ExifProfile ??= new ExifProfile();
            exif.SetValue(ExifTag.ImageDescription, "Clop Windows Test");
        }

        image.Save(path.Value, GetEncoder(extension));
        return path;
    }

    private static IImageEncoder GetEncoder(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            "png" => new PngEncoder(),
            "bmp" => new BmpEncoder(),
            _ => new JpegEncoder { Quality = 85 }
        };
    }

    private static void CleanupFiles(FilePath source, IEnumerable<FilePath> outputs)
    {
        TryDelete(source);
        foreach (var output in outputs)
        {
            TryDelete(output);
        }
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
