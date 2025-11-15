using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClopWindows.Core.Shared;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace ClopWindows.Core.Optimizers;

internal sealed class CropSuggestionService
{
    private readonly CropSuggestionOptions _options;
    private readonly string? _onnxModelPath;
    private readonly object _sessionGate = new();
    private InferenceSession? _onnxSession;
    private bool _onnxUnavailable;

    public CropSuggestionService(CropSuggestionOptions options)
    {
        _options = options ?? CropSuggestionOptions.Disabled;
        if (!string.IsNullOrWhiteSpace(options?.OnnxModelPath) && File.Exists(options.OnnxModelPath))
        {
            _onnxModelPath = options.OnnxModelPath;
        }
    }

    public CropSuggestionResult Generate(FilePath sourcePath, Image<Rgba32> processedImage)
    {
        if (!_options.Enabled)
        {
            return CropSuggestionResult.Empty;
        }

        var cacheKey = BuildCacheKey(sourcePath);
        var cacheDirectory = ClopPaths.SegmentationCache.Append(cacheKey);
        cacheDirectory.EnsurePathExists();

        var suggestionsPath = cacheDirectory.Append("suggestions.json");
        var maskPath = cacheDirectory.Append("mask.png");

        if (_options.CacheMasks && TryLoadFromCache(suggestionsPath, maskPath, out var cached))
        {
            return cached;
        }

        var suggestions = Analyse(processedImage);

        if (suggestions.Crops.Count > 0 && _options.CacheMasks)
        {
            if (suggestions.MaskData is not null)
            {
                SaveMask(suggestions.MaskData, maskPath);
                suggestions = new CropSuggestionResult(suggestions.Crops, maskPath, null);
            }
            else
            {
                SaveMask(processedImage, suggestions.Crops, maskPath);
                suggestions = new CropSuggestionResult(suggestions.Crops, maskPath, null);
            }

            PersistSuggestions(suggestions, suggestionsPath, maskPath);
        }

        return suggestions;
    }

    private CropSuggestionResult Analyse(Image<Rgba32> image)
    {
        if (_onnxModelPath is not null && TrySegmentWithOnnx(image, out var onnxResult))
        {
            return onnxResult;
        }

        var bounds = DetectForegroundBounds(image);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return CropSuggestionResult.Empty;
        }

        var area = bounds.Width * bounds.Height;
        if (area < _options.MinimumSubjectAreaPixels)
        {
            return CropSuggestionResult.Empty;
        }

        var crop = new CropSuggestion(bounds.X, bounds.Y, bounds.Width, bounds.Height, "Foreground subject");
        return new CropSuggestionResult(new[] { crop }, null);
    }

    private Rectangle DetectForegroundBounds(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var threshold = 0.88 - Math.Clamp(_options.EdgeSensitivity, 0, 1) * 0.3;

        var top = -1;
        var bottom = -1;
        var left = width;
        var right = -1;

        for (var y = 0; y < height; y++)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            var rowActive = false;

            for (var x = 0; x < width; x++)
            {
                var pixel = row[x];
                if (pixel.A < 12)
                {
                    continue;
                }

                var luma = ToLuminance(pixel);
                if (luma < threshold)
                {
                    rowActive = true;
                    if (x < left)
                    {
                        left = x;
                    }

                    if (x > right)
                    {
                        right = x;
                    }
                }
            }

            if (!rowActive)
            {
                continue;
            }

            if (top < 0)
            {
                top = y;
            }
            bottom = y;
        }

        if (top < 0 || right < 0)
        {
            return Rectangle.Empty;
        }

        left = Math.Max(0, left);
        right = Math.Min(width - 1, right);

        var rectWidth = right - left + 1;
        var rectHeight = bottom - top + 1;

        return new Rectangle(left, top, rectWidth, rectHeight);
    }

    private bool TrySegmentWithOnnx(Image<Rgba32> image, out CropSuggestionResult result)
    {
        result = CropSuggestionResult.Empty;
        var session = GetOnnxSession();
        if (session is null)
        {
            return false;
        }

        try
        {
            var inputInfo = session.InputMetadata.First();
            var inputDims = inputInfo.Value.Dimensions;
            var expectedHeight = inputDims.Length >= 4 && inputDims[^2] > 0 ? inputDims[^2] : image.Height;
            var expectedWidth = inputDims.Length >= 4 && inputDims[^1] > 0 ? inputDims[^1] : image.Width;

            var tensor = new DenseTensor<float>(new[] { 1, 3, expectedHeight, expectedWidth });
            for (var y = 0; y < expectedHeight; y++)
            {
                var sourceY = Math.Min((int)Math.Round(y * (image.Height / (double)expectedHeight)), image.Height - 1);
                var row = image.DangerousGetPixelRowMemory(sourceY).Span;
                for (var x = 0; x < expectedWidth; x++)
                {
                    var sourceX = Math.Min((int)Math.Round(x * (image.Width / (double)expectedWidth)), image.Width - 1);
                    var pixel = row[sourceX];
                    tensor[0, 0, y, x] = pixel.R / 255f;
                    tensor[0, 1, y, x] = pixel.G / 255f;
                    tensor[0, 2, y, x] = pixel.B / 255f;
                }
            }

            var input = NamedOnnxValue.CreateFromTensor(inputInfo.Key, tensor);
            try
            {
                var inputs = new[] { input };
                using var outputs = session.Run(inputs);
                var outputTensor = outputs.First().AsTensor<float>();
                var outputDims = outputTensor.Dimensions.ToArray();

                var maskHeight = outputDims.Length >= 2 ? outputDims[^2] : image.Height;
                var maskWidth = outputDims.Length >= 1 ? outputDims[^1] : image.Width;

                var binaryMask = new bool[image.Height, image.Width];
                for (var y = 0; y < image.Height; y++)
                {
                    var mappedY = Math.Min(y * maskHeight / image.Height, maskHeight - 1);
                    for (var x = 0; x < image.Width; x++)
                    {
                        var mappedX = Math.Min(x * maskWidth / image.Width, maskWidth - 1);
                        float value;
                        if (outputDims.Length == 4)
                        {
                            value = outputTensor[0, 0, mappedY, mappedX];
                        }
                        else if (outputDims.Length == 3)
                        {
                            value = outputTensor[0, mappedY, mappedX];
                        }
                        else
                        {
                            value = outputTensor[mappedY * maskWidth + mappedX];
                        }

                        binaryMask[y, x] = value >= 0.5f;
                    }
                }

                var bounds = ExtractBoundsFromMask(binaryMask);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    return false;
                }

                var crop = new CropSuggestion(bounds.X, bounds.Y, bounds.Width, bounds.Height, "Foreground subject");
                result = new CropSuggestionResult(new[] { crop }, null, binaryMask);
                return true;
            }
            finally
            {
                if (input is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"ONNX segmentation failed: {ex.Message}");
            return false;
        }
    }

    private InferenceSession? GetOnnxSession()
    {
        if (_onnxModelPath is null || _onnxUnavailable)
        {
            return null;
        }

        lock (_sessionGate)
        {
            if (_onnxSession is not null)
            {
                return _onnxSession;
            }

            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED
                };
                _onnxSession = new InferenceSession(_onnxModelPath, options);
            }
            catch (Exception ex)
            {
                _onnxUnavailable = true;
                Log.Warning($"Unable to initialise ONNX segmentation model: {ex.Message}");
                return null;
            }

            return _onnxSession;
        }
    }

    private static Rectangle ExtractBoundsFromMask(bool[,] mask)
    {
        var height = mask.GetLength(0);
        var width = mask.GetLength(1);
        var top = -1;
        var bottom = -1;
        var left = width;
        var right = -1;

        for (var y = 0; y < height; y++)
        {
            var rowActive = false;
            for (var x = 0; x < width; x++)
            {
                if (!mask[y, x])
                {
                    continue;
                }

                rowActive = true;
                if (x < left)
                {
                    left = x;
                }

                if (x > right)
                {
                    right = x;
                }
            }

            if (!rowActive)
            {
                continue;
            }

            if (top < 0)
            {
                top = y;
            }
            bottom = y;
        }

        if (top < 0 || right < 0)
        {
            return Rectangle.Empty;
        }

        return new Rectangle(left, top, right - left + 1, bottom - top + 1);
    }

    private static void SaveMask(Image<Rgba32> image, IReadOnlyList<CropSuggestion> crops, FilePath maskPath)
    {
        using var mask = new Image<L8>(image.Width, image.Height);
        var rectangles = crops.Select(c => new Rectangle(c.X, c.Y, c.Width, c.Height)).ToArray();

        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < mask.Height; y++)
            {
                var span = accessor.GetRowSpan(y);
                for (var x = 0; x < mask.Width; x++)
                {
                    var inside = rectangles.Any(rect => rect.Contains(x, y));
                    span[x] = inside ? new L8(byte.MaxValue) : new L8(0);
                }
            }
        });

        mask.Save(maskPath.Value);
    }

    private static void SaveMask(bool[,] binaryMask, FilePath maskPath)
    {
        var height = binaryMask.GetLength(0);
        var width = binaryMask.GetLength(1);

        using var mask = new Image<L8>(width, height);
        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    row[x] = binaryMask[y, x] ? new L8(byte.MaxValue) : new L8(0);
                }
            }
        });

        mask.Save(maskPath.Value);
    }

    private static bool TryLoadFromCache(FilePath suggestionsPath, FilePath maskPath, out CropSuggestionResult result)
    {
        result = CropSuggestionResult.Empty;
        try
        {
            if (!File.Exists(suggestionsPath.Value))
            {
                return false;
            }

            var payload = File.ReadAllText(suggestionsPath.Value);
            var dto = JsonSerializer.Deserialize<CropSuggestionCacheDto>(payload);
            if (dto is null)
            {
                return false;
            }

            var crops = dto.Crops.Select(c => new CropSuggestion(c.X, c.Y, c.Width, c.Height, c.Label)).ToArray();
            FilePath? mask = dto.MaskExists && File.Exists(maskPath.Value) ? maskPath : (FilePath?)null;
            result = new CropSuggestionResult(crops, mask);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PersistSuggestions(CropSuggestionResult result, FilePath path, FilePath maskPath)
    {
        try
        {
            var dto = new CropSuggestionCacheDto
            {
                Crops = result.Crops.Select(c => new CropSuggestionCacheDto.Crop(c.X, c.Y, c.Width, c.Height, c.Label)).ToArray(),
                MaskExists = result.MaskPath is not null,
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path.Value, json);
        }
        catch
        {
            // ignore persistence failures
        }
    }

    private static string BuildCacheKey(FilePath source)
    {
        var fileName = Path.GetFileNameWithoutExtension(source.Value);
        var stamp = File.Exists(source.Value) ? File.GetLastWriteTimeUtc(source.Value).Ticks.ToString() : "na";
        return string.Join("-", fileName, stamp).Replace(Path.DirectorySeparatorChar, '-').Replace(Path.AltDirectorySeparatorChar, '-');
    }

    private static double ToLuminance(Rgba32 pixel)
    {
        const double scale = 1.0 / 255.0;
        return (pixel.R * 0.299 * scale) + (pixel.G * 0.587 * scale) + (pixel.B * 0.114 * scale);
    }

    private sealed class CropSuggestionCacheDto
    {
        public CropSuggestionCacheDto.Crop[] Crops { get; set; } = Array.Empty<Crop>();
        public bool MaskExists { get; set; }

        public sealed record Crop(int X, int Y, int Width, int Height, string Label);
    }
}

internal sealed record CropSuggestion(int X, int Y, int Width, int Height, string Label);

internal sealed record CropSuggestionResult(IReadOnlyList<CropSuggestion> Crops, FilePath? MaskPath, bool[,]? MaskData = null)
{
    public static CropSuggestionResult Empty { get; } = new(Array.Empty<CropSuggestion>(), null, null);
}
