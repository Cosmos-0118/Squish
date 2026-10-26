using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squish.Core.Shared;

public enum SquishErrorKind
{
    FileNotFound,
    FileNotImage,
    NoClipboardImage,
    NoProcess,
    AlreadyOptimised,
    AlreadyResized,
    ImageSizeLarger,
    VideoSizeLarger,
    UnknownImageType,
    VideoError,
    DownloadError,
    SkippedType,
    OptimisationPaused,
    OptimisationFailed,
    ConversionFailed,
    ProError,
    DecompressingBinariesError,
    DownscaleFailed,
    AppNotRunning,
    CouldNotCreateOutputDirectory,
    UnknownType
}

public readonly record struct SquishError(SquishErrorKind Kind, FilePath? Path = null, string? Message = null)
{
    public string Description => Kind switch
    {
        SquishErrorKind.FileNotFound => $"File not found: {DescribePath(Path)}",
        SquishErrorKind.FileNotImage => $"File is not an image: {DescribePath(Path)}",
        SquishErrorKind.NoClipboardImage => DescribeClipboard(Path),
        SquishErrorKind.NoProcess => $"Can't start process: {Message ?? string.Empty}",
        SquishErrorKind.AlreadyOptimised => $"Image is already optimised: {DescribePath(Path)}",
        SquishErrorKind.AlreadyResized => $"Image is already at the correct size or smaller: {DescribePath(Path)}",
        SquishErrorKind.ImageSizeLarger => $"Optimised image size is larger: {DescribePath(Path)}",
        SquishErrorKind.VideoSizeLarger => $"Optimised video size is larger: {DescribePath(Path)}",
        SquishErrorKind.UnknownImageType => $"Unknown image type: {DescribePath(Path)}",
        SquishErrorKind.VideoError => $"Error processing video: {Message ?? string.Empty}",
        SquishErrorKind.DownloadError => $"Download failed: {Message ?? string.Empty}",
        SquishErrorKind.SkippedType => $"Type is skipped: {Message ?? string.Empty}",
        SquishErrorKind.OptimisationPaused => $"Optimisation paused: {DescribePath(Path)}",
        SquishErrorKind.OptimisationFailed => $"Optimisation failed: {Message ?? string.Empty}",
        SquishErrorKind.ConversionFailed => $"Conversion failed: {DescribePath(Path)}",
        SquishErrorKind.ProError => $"Pro error: {Message ?? string.Empty}",
        SquishErrorKind.DecompressingBinariesError => "Decompressing binaries",
        SquishErrorKind.DownscaleFailed => $"Downscale failed: {DescribePath(Path)}",
        SquishErrorKind.AppNotRunning => $"App is not running, integration failed: {DescribePath(Path)}",
        SquishErrorKind.CouldNotCreateOutputDirectory => $"Could not create output directory: {Message ?? string.Empty}",
        SquishErrorKind.UnknownType => "Unknown type",
        _ => Message ?? Kind.ToString()
    };

    public string HumanDescription => Kind switch
    {
        SquishErrorKind.FileNotFound => "File not found",
        SquishErrorKind.FileNotImage => "Not an image",
        SquishErrorKind.NoClipboardImage => "No image in clipboard",
        SquishErrorKind.NoProcess => "Can't start process",
        SquishErrorKind.AlreadyOptimised => "Already optimised",
        SquishErrorKind.AlreadyResized => "Image is already at the correct size or smaller",
        SquishErrorKind.ImageSizeLarger => "Already optimised",
        SquishErrorKind.VideoSizeLarger => "Already optimised",
        SquishErrorKind.UnknownImageType => "Unknown image type",
        SquishErrorKind.VideoError => "Video error",
        SquishErrorKind.DownloadError => "Download failed",
        SquishErrorKind.SkippedType => "Type is skipped",
        SquishErrorKind.OptimisationPaused => "Optimisation paused",
        SquishErrorKind.ConversionFailed => "Conversion failed",
        SquishErrorKind.ProError => "Pro error",
        SquishErrorKind.DownscaleFailed => "Downscale failed",
        SquishErrorKind.OptimisationFailed => "Optimisation failed",
        SquishErrorKind.AppNotRunning => "App integration not running",
        SquishErrorKind.CouldNotCreateOutputDirectory => "Could not create output directory",
        SquishErrorKind.DecompressingBinariesError => "Decompressing binaries",
        SquishErrorKind.UnknownType => "Unknown type",
        _ => Kind.ToString()
    };

    public override string ToString() => Description;

    private static string DescribeClipboard(FilePath? path)
    {
        if (path is null || string.IsNullOrWhiteSpace(path.Value.Value))
        {
            return "No image in clipboard";
        }
        var value = path.Value.Value;
        if (value.Length <= 100)
        {
            return $"No image in clipboard: {value}";
        }
        var prefix = value[..50];
        var suffix = value[^50..];
        return $"No image in clipboard: {prefix}...{suffix}";
    }

    private static string DescribePath(FilePath? path) => path?.Value ?? "<unknown>";
}

public static class SharedConstants
{
    public const string OptimisationPortId = "Squish.optimisationService";
    public const string OptimisationStopPortId = "Squish.optimisationServiceStop";
    public const string OptimisationResponsePortId = "Squish.optimisationServiceResponse";
    public const string OptimisationCliResponsePortId = "Squish.optimisationServiceResponseCLI";
}

public static class MediaFormats
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mov", "mp4", "webm", "mkv", "mpg", "mpeg", "m2v", "avi", "m4v", "wmv", "flv"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "webp", "avif", "heic", "bmp", "tiff", "tif", "png", "jpeg", "jpg", "gif"
    };

    public static IReadOnlyCollection<string> ImageExtensionNames => ImageExtensions;

    public static IReadOnlyCollection<string> VideoExtensionNames => VideoExtensions;

    public static IReadOnlySet<string> ImageVideoFormats { get; } = ImageExtensions.Union(VideoExtensions, StringComparer.OrdinalIgnoreCase).ToHashSet();

    public static bool IsImage(FilePath path) => IsImage(path.Extension);

    public static bool IsImage(string? extension) => !string.IsNullOrWhiteSpace(extension) && ImageExtensions.Contains(extension.TrimStart('.'));

    public static bool IsVideo(FilePath path) => IsVideo(path.Extension);

    public static bool IsVideo(string? extension) => !string.IsNullOrWhiteSpace(extension) && VideoExtensions.Contains(extension.TrimStart('.'));
}

public enum CropOrientation
{
    Landscape,
    Portrait,
    Adaptive
}

public readonly record struct CropSize
{
    public CropSize(int width, int height, string name = "", bool longEdge = false, bool smartCrop = false, bool isAspectRatio = false)
    {
        Width = width;
        Height = height;
        Name = name;
        LongEdge = longEdge;
        SmartCrop = smartCrop;
        IsAspectRatio = isAspectRatio;
    }

    public int Width { get; init; }

    public int Height { get; init; }

    public string Name { get; init; }

    public bool LongEdge { get; init; }

    public bool SmartCrop { get; init; }

    public bool IsAspectRatio { get; init; }

    public static CropSize Zero { get; } = new(0, 0);

    [JsonIgnore]
    public CropSize Flipped
    {
        get
        {
            var flippedName = Name;
            if (Name.Contains(':', StringComparison.Ordinal))
            {
                var parts = Name.Split(':');
                flippedName = $"{parts.Last()}:{parts.First()}";
            }
            return new CropSize(Height, Width, flippedName, LongEdge, SmartCrop, IsAspectRatio);
        }
    }

    [JsonIgnore]
    public CropOrientation Orientation => Width >= Height ? CropOrientation.Landscape : CropOrientation.Portrait;

    [JsonIgnore]
    public double FractionalAspectRatio => Math.Min(Width, Height) / (double)Math.Max(Width == 0 ? 1 : Width, Height == 0 ? 1 : Height);

    [JsonIgnore]
    public string Id => $"{(Width == 0 ? "Auto" : Width.ToInvariantString())}×{(Height == 0 ? "Auto" : Height.ToInvariantString())}";

    [JsonIgnore]
    public int Area
    {
        get
        {
            var w = Width == 0 ? Height : Width;
            var h = Height == 0 ? Width : Height;
            return w * h;
        }
    }

    public SizeD AsSizeD() => new(Width, Height);

    public CropSize WithLongEdge(bool value) => this with { LongEdge = value };

    public CropSize WithSmartCrop(bool value) => this with { SmartCrop = value };

    public CropSize WithOrientation(CropOrientation orientation, SizeD? reference = null) => orientation switch
    {
        CropOrientation.Landscape => (Width >= Height ? this : Flipped).WithLongEdge(false),
        CropOrientation.Portrait => (Width >= Height ? Flipped : this).WithLongEdge(false),
        CropOrientation.Adaptive when reference.HasValue => (reference.Value.Orientation == Orientation ? this : Flipped).WithLongEdge(true),
        CropOrientation.Adaptive => WithLongEdge(true),
        _ => this
    };

    public double FactorFrom(SizeD size)
    {
        if (IsAspectRatio)
        {
            var computed = ComputedSize(size);
            return (computed.Width * computed.Height) / (size.Width * size.Height);
        }
        if (LongEdge)
        {
            var longEdge = Width == 0 ? Height : Width;
            return longEdge / Math.Max(size.Width, size.Height);
        }
        if (Width == 0)
        {
            return Height / size.Height;
        }
        if (Height == 0)
        {
            return Width / size.Width;
        }
        return (Width * Height) / (size.Width * size.Height);
    }

    public SizeD ComputedSize(SizeD size)
    {
        if (!IsAspectRatio && !LongEdge && Width != 0 && Height != 0)
        {
            return new SizeD(Width, Height);
        }
        if (IsAspectRatio)
        {
            var alwaysPortrait = !LongEdge && Width < Height;
            var alwaysLandscape = !LongEdge && Height < Width;
            return size.CropTo(FractionalAspectRatio, alwaysPortrait, alwaysLandscape);
        }
        return size.Scale(FactorFrom(size));
    }
}

public readonly record struct SizeD(double Width, double Height)
{
    public bool IsLandscape => Width > Height;

    public bool IsPortrait => Height > Width;

    public CropOrientation Orientation => Width >= Height ? CropOrientation.Landscape : CropOrientation.Portrait;

    public SizeD Flipped => new(Height, Width);

    public SizeD Scale(double factor) => new((Width * factor).EvenInt(), (Height * factor).EvenInt());

    public SizeD CropTo(double aspectRatio, bool alwaysPortrait = false, bool alwaysLandscape = false)
    {
        if (alwaysPortrait)
        {
            return CropToPortrait(aspectRatio);
        }
        if (alwaysLandscape)
        {
            return CropToLandscape(aspectRatio);
        }
        return IsLandscape ? CropToLandscape(aspectRatio) : CropToPortrait(aspectRatio);
    }

    private SizeD CropToPortrait(double aspectRatio)
    {
        var selfAspectRatio = Width / Height;
        if (selfAspectRatio > aspectRatio)
        {
            var width = Height * aspectRatio;
            return new SizeD(width, Height);
        }
        var height = Width / aspectRatio;
        return new SizeD(Width, height);
    }

    private SizeD CropToLandscape(double aspectRatio)
    {
        var selfAspectRatio = Height / Width;
        if (selfAspectRatio > aspectRatio)
        {
            var height = Width * aspectRatio;
            return new SizeD(Width, height);
        }
        var width = Height / aspectRatio;
        return new SizeD(width, Height);
    }
}

public static class CropLibraries
{
    public static IReadOnlyDictionary<string, CropSize> PaperSizes { get; }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, CropSize>> PaperCropSizes { get; }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, CropSize>> DeviceCropSizes { get; }

    static CropLibraries()
    {
        PaperSizes = SharedDataLoader.PaperSizesByCategory
            .SelectMany(category => category.Value.Select(entry => new CropSizeEntry(entry.Key, entry.Value)))
            .ToDictionary(entry => entry.Name, entry => entry.Size);

        PaperCropSizes = SharedDataLoader.PaperSizesByCategory
            .ToDictionary(
                category => category.Key,
                category => (IReadOnlyDictionary<string, CropSize>)category.Value.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ToCropSize(entry.Key, isAspectRatio: true)
                ));

        DeviceCropSizes = SharedDataLoader.DeviceSizes
            .GroupBy(entry => entry.Key.Split(' ')[0])
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, CropSize>)group.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.ToCropSize(entry.Key, isAspectRatio: true)
                ));
    }

    private readonly record struct CropSizeEntry(string Name, CropSize Size)
    {
        public CropSizeEntry(string name, SizeD size)
            : this(name, size.ToCropSize(name, isAspectRatio: true))
        {
        }
    }
}

internal static class SharedDataLoader
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, SizeD>> PaperSizesByCategory { get; }

    public static IReadOnlyDictionary<string, SizeD> DeviceSizes { get; }

    static SharedDataLoader()
    {
        PaperSizesByCategory = LoadNestedDictionary("paper_sizes.json");
        DeviceSizes = LoadDictionary("device_sizes.json");
    }

    private static IReadOnlyDictionary<string, SizeD> LoadDictionary(string resourceName)
    {
        using var stream = OpenResource(resourceName);
        var document = JsonDocument.Parse(stream);
        var builder = ImmutableDictionary.CreateBuilder<string, SizeD>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            builder[property.Name] = ReadSize(property.Value);
        }
        return builder.ToImmutable();
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, SizeD>> LoadNestedDictionary(string resourceName)
    {
        using var stream = OpenResource(resourceName);
        var document = JsonDocument.Parse(stream);
        var builder = ImmutableDictionary.CreateBuilder<string, IReadOnlyDictionary<string, SizeD>>(StringComparer.Ordinal);
        foreach (var category in document.RootElement.EnumerateObject())
        {
            var innerBuilder = ImmutableDictionary.CreateBuilder<string, SizeD>(StringComparer.Ordinal);
            foreach (var entry in category.Value.EnumerateObject())
            {
                innerBuilder[entry.Name] = ReadSize(entry.Value);
            }
            builder[category.Name] = innerBuilder.ToImmutable();
        }
        return builder.ToImmutable();
    }

    private static Stream OpenResource(string resourceName)
    {
        var assembly = typeof(SharedDataLoader).GetTypeInfo().Assembly;
        var fullName = assembly.GetManifestResourceNames().Single(name => name.EndsWith($"Shared.Data.{resourceName}", StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(fullName) ?? throw new InvalidOperationException($"Cannot load resource {resourceName}");
    }

    private static SizeD ReadSize(JsonElement element)
    {
        var width = element[0].GetDouble();
        var height = element[1].GetDouble();
        return new SizeD(width, height);
    }
}

internal static class SizeExtensions
{
    public static CropSize ToCropSize(this SizeD size, string name, bool isAspectRatio) => new(size.Width.Intround(), size.Height.Intround(), name, isAspectRatio: isAspectRatio);
}
