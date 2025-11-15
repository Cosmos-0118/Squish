using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClopWindows.Core.Shared;

public enum ClopErrorKind
{
    FileNotFound,
    FileNotImage,
    NoClipboardImage,
    NoProcess,
    AlreadyOptimised,
    AlreadyResized,
    ImageSizeLarger,
    VideoSizeLarger,
    PdfSizeLarger,
    UnknownImageType,
    VideoError,
    PdfError,
    DownloadError,
    SkippedType,
    OptimisationPaused,
    OptimisationFailed,
    ConversionFailed,
    ProError,
    DecompressingBinariesError,
    DownscaleFailed,
    AppNotRunning,
    EncryptedPdf,
    InvalidPdf,
    CouldNotCreateOutputDirectory,
    UnknownType
}

public readonly record struct ClopError(ClopErrorKind Kind, FilePath? Path = null, string? Message = null)
{
    public string Description => Kind switch
    {
        ClopErrorKind.FileNotFound => $"File not found: {DescribePath(Path)}",
        ClopErrorKind.FileNotImage => $"File is not an image: {DescribePath(Path)}",
        ClopErrorKind.NoClipboardImage => DescribeClipboard(Path),
        ClopErrorKind.NoProcess => $"Can't start process: {Message ?? string.Empty}",
        ClopErrorKind.AlreadyOptimised => $"Image is already optimised: {DescribePath(Path)}",
        ClopErrorKind.AlreadyResized => $"Image is already at the correct size or smaller: {DescribePath(Path)}",
        ClopErrorKind.ImageSizeLarger => $"Optimised image size is larger: {DescribePath(Path)}",
        ClopErrorKind.VideoSizeLarger => $"Optimised video size is larger: {DescribePath(Path)}",
        ClopErrorKind.PdfSizeLarger => $"Optimised PDF size is larger: {DescribePath(Path)}",
        ClopErrorKind.UnknownImageType => $"Unknown image type: {DescribePath(Path)}",
        ClopErrorKind.VideoError => $"Error processing video: {Message ?? string.Empty}",
        ClopErrorKind.PdfError => $"Error processing PDF: {Message ?? string.Empty}",
        ClopErrorKind.DownloadError => $"Download failed: {Message ?? string.Empty}",
        ClopErrorKind.SkippedType => $"Type is skipped: {Message ?? string.Empty}",
        ClopErrorKind.OptimisationPaused => $"Optimisation paused: {DescribePath(Path)}",
        ClopErrorKind.OptimisationFailed => $"Optimisation failed: {Message ?? string.Empty}",
        ClopErrorKind.ConversionFailed => $"Conversion failed: {DescribePath(Path)}",
        ClopErrorKind.ProError => $"Pro error: {Message ?? string.Empty}",
        ClopErrorKind.DecompressingBinariesError => "Decompressing binaries",
        ClopErrorKind.DownscaleFailed => $"Downscale failed: {DescribePath(Path)}",
        ClopErrorKind.AppNotRunning => $"App is not running, integration failed: {DescribePath(Path)}",
        ClopErrorKind.EncryptedPdf => $"PDF is encrypted: {DescribePath(Path)}",
        ClopErrorKind.InvalidPdf => $"Can't parse PDF: {DescribePath(Path)}",
        ClopErrorKind.CouldNotCreateOutputDirectory => $"Could not create output directory: {Message ?? string.Empty}",
        ClopErrorKind.UnknownType => "Unknown type",
        _ => Message ?? Kind.ToString()
    };

    public string HumanDescription => Kind switch
    {
        ClopErrorKind.FileNotFound => "File not found",
        ClopErrorKind.FileNotImage => "Not an image",
        ClopErrorKind.NoClipboardImage => "No image in clipboard",
        ClopErrorKind.NoProcess => "Can't start process",
        ClopErrorKind.AlreadyOptimised => "Already optimised",
        ClopErrorKind.AlreadyResized => "Image is already at the correct size or smaller",
        ClopErrorKind.ImageSizeLarger => "Already optimised",
        ClopErrorKind.VideoSizeLarger => "Already optimised",
        ClopErrorKind.PdfSizeLarger => "Already optimised",
        ClopErrorKind.UnknownImageType => "Unknown image type",
        ClopErrorKind.VideoError => "Video error",
        ClopErrorKind.PdfError => "PDF error",
        ClopErrorKind.DownloadError => "Download failed",
        ClopErrorKind.SkippedType => "Type is skipped",
        ClopErrorKind.OptimisationPaused => "Optimisation paused",
        ClopErrorKind.ConversionFailed => "Conversion failed",
        ClopErrorKind.ProError => "Pro error",
        ClopErrorKind.DownscaleFailed => "Downscale failed",
        ClopErrorKind.OptimisationFailed => "Optimisation failed",
        ClopErrorKind.AppNotRunning => "App integration not running",
        ClopErrorKind.EncryptedPdf => "PDF is encrypted",
        ClopErrorKind.InvalidPdf => "Can't parse PDF",
        ClopErrorKind.CouldNotCreateOutputDirectory => "Could not create output directory",
        ClopErrorKind.DecompressingBinariesError => "Decompressing binaries",
        ClopErrorKind.UnknownType => "Unknown type",
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
    public const string OptimisationPortId = "com.lowtechguys.Clop.optimisationService";
    public const string OptimisationStopPortId = "com.lowtechguys.Clop.optimisationServiceStop";
    public const string OptimisationResponsePortId = "com.lowtechguys.Clop.optimisationServiceResponse";
    public const string OptimisationCliResponsePortId = "com.lowtechguys.Clop.optimisationServiceResponseCLI";
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

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf"
    };

    public static IReadOnlyCollection<string> ImageExtensionNames => ImageExtensions;

    public static IReadOnlyCollection<string> VideoExtensionNames => VideoExtensions;

    public static IReadOnlyCollection<string> PdfExtensionNames => PdfExtensions;

    public static IReadOnlySet<string> ImageVideoFormats { get; } = ImageExtensions.Union(VideoExtensions, StringComparer.OrdinalIgnoreCase).ToHashSet();

    public static bool IsImage(FilePath path) => IsImage(path.Extension);

    public static bool IsImage(string? extension) => !string.IsNullOrWhiteSpace(extension) && ImageExtensions.Contains(extension.TrimStart('.'));

    public static bool IsVideo(FilePath path) => IsVideo(path.Extension);

    public static bool IsVideo(string? extension) => !string.IsNullOrWhiteSpace(extension) && VideoExtensions.Contains(extension.TrimStart('.'));

    public static bool IsPdf(FilePath path) => IsPdf(path.Extension);

    public static bool IsPdf(string? extension) => !string.IsNullOrWhiteSpace(extension) && PdfExtensions.Contains(extension.TrimStart('.'));
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
