using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public sealed record PdfOptimiserOptions
{
    public static PdfOptimiserOptions Default { get; } = new();

    private static readonly TimeSpan InstallationCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly object InstallationLock = new();
    private static GhostscriptInstallation? _cachedInstallation;
    private static DateTimeOffset _cachedInstallationTimestamp;

    public string GhostscriptPath { get; init; } = ResolveGhostscriptPath();

    public string? GhostscriptResourceDirectory { get; init; } = ResolveGhostscriptResourceDirectory();

    public string FontSearchPath { get; init; } = ResolveFontSearchPath();

    public bool AggressiveByDefault { get; init; } = false;

    public bool StripMetadata { get; init; } = true;

    public bool PreserveTimestamps { get; init; } = true;

    public bool RequireSmallerSize { get; init; } = true;

    public bool EnableLinearisation { get; init; } = true;

    public bool UseWindowsColorProfiles { get; init; } = true;

    public int ProbeSizeBytes { get; init; } = 1_500_000;

    public int LongDocumentPageThreshold { get; init; } = 60;

    public double ImageDensityThreshold { get; init; } = 1.1d;

    public double HighImageDpiThreshold { get; init; } = 220d;

    public IReadOnlyList<string> BaseArguments { get; init; } = DefaultGhostscriptArguments.Base;

    public IReadOnlyList<string> LossyArguments { get; init; } = DefaultGhostscriptArguments.Lossy;

    public IReadOnlyList<string> LosslessArguments { get; init; } = DefaultGhostscriptArguments.Lossless;

    public IReadOnlyList<string> MetadataPreArguments { get; init; } = DefaultGhostscriptArguments.MetadataPre;

    public IReadOnlyList<string> MetadataPostArguments { get; init; } = DefaultGhostscriptArguments.MetadataPost;

    public IReadOnlyList<string> TextPresetArguments { get; init; } = PdfPresetArguments.Text;

    public IReadOnlyList<string> MixedPresetArguments { get; init; } = PdfPresetArguments.Mixed;

    public IReadOnlyList<string> GraphicsPresetArguments { get; init; } = PdfPresetArguments.Graphics;

    public string? QpdfPath { get; init; } = ResolveQpdfPath();

    public PdfOptimiserOptions RefreshGhostscript()
    {
        var refreshedPath = ResolveGhostscriptPath(forceRefresh: true);
        var refreshedResource = ResolveGhostscriptResourceDirectory(forceRefresh: true);
        var refreshedQpdf = ResolveQpdfPath(forceRefresh: true);
        return this with
        {
            GhostscriptPath = refreshedPath,
            GhostscriptResourceDirectory = refreshedResource,
            QpdfPath = refreshedQpdf
        };
    }

    private static string ResolveGhostscriptPath(bool forceRefresh = false)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("CLOP_GS") ?? Environment.GetEnvironmentVariable("GS_EXECUTABLE");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env!;
            }

            var baseDir = GetBaseDirectory();
            var ghostscriptSegments = new[]
            {
                new[] { "tools", "ghostscript", "gswin64c.exe" },
                new[] { "tools", "ghostscript", "bin", "gswin64c.exe" }
            };

            foreach (var segments in ghostscriptSegments)
            {
                var candidate = ToolLocator.EnumeratePossibleFiles(baseDir, segments).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate!;
                }
            }

            var installation = GetCachedInstallation(forceRefresh);
            if (installation is not null && !string.IsNullOrWhiteSpace(installation.ExecutablePath))
            {
                return installation.ExecutablePath;
            }
        }
        catch
        {
            // fall back to default executable name below
        }

        return "gswin64c.exe";
    }

    private static string? ResolveGhostscriptResourceDirectory(bool forceRefresh = false)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("CLOP_GS_LIB") ?? Environment.GetEnvironmentVariable("GS_LIB");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env!;
            }

            var baseDir = GetBaseDirectory();
            foreach (var path in ToolLocator.EnumeratePossibleDirectories(baseDir, new[] { "tools", "ghostscript", "Resource", "Init" }))
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path!;
                }
            }

            return GetCachedInstallation(forceRefresh)?.ResourceDirectory;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveFontSearchPath()
    {
        var env = Environment.GetEnvironmentVariable("CLOP_FONT_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env!;
        }

        var paths = new[]
        {
            CombineSafe(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            CombineSafe(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Adobe", "Fonts")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path!))
        .Select(path => path!);

        var joined = string.Join(';', paths);
        if (!string.IsNullOrWhiteSpace(joined))
        {
            return joined;
        }

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return string.IsNullOrWhiteSpace(fallback) ? "C:/Windows/Fonts" : fallback;
    }

    private static string? ResolveQpdfPath(bool forceRefresh = false)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("CLOP_QPDF") ?? Environment.GetEnvironmentVariable("QPDF_EXECUTABLE");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env!;
            }

            var baseDir = GetBaseDirectory();
            var qpdfSegments = new[]
            {
                new[] { "tools", "qpdf", "qpdf.exe" },
                new[] { "tools", "qpdf", "bin", "qpdf.exe" }
            };

            foreach (var segments in qpdfSegments)
            {
                var candidate = ToolLocator.EnumeratePossibleFiles(baseDir, segments).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate!;
                }
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                var exe = Path.Combine(programFiles!, "qpdf", "bin", "qpdf.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }
        catch
        {
            // fall through to default executable name
        }

        return "qpdf.exe";
    }

    private static string? CombineSafe(string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return ToolLocator.SafeCombine(root!, segments);
    }

    private static GhostscriptInstallation? GetCachedInstallation(bool forceRefresh)
    {
        lock (InstallationLock)
        {
            var now = DateTimeOffset.UtcNow;
            var hasCached = _cachedInstallationTimestamp != default;
            var cachedStillValid = hasCached && now - _cachedInstallationTimestamp < InstallationCacheDuration;
            var cachedMissing = _cachedInstallation is not null && !File.Exists(_cachedInstallation.ExecutablePath);

            if (!forceRefresh && hasCached && cachedStillValid && !cachedMissing)
            {
                return _cachedInstallation;
            }

            var installation = DiscoverGhostscriptInstallation();

            if (installation is null)
            {
                _cachedInstallation = null;
                _cachedInstallationTimestamp = default;
            }
            else
            {
                _cachedInstallation = installation;
                _cachedInstallationTimestamp = now;
            }

            return installation;
        }
    }

    private static GhostscriptInstallation? DiscoverGhostscriptInstallation()
    {
        foreach (var executable in EnumerateInstalledExecutables())
        {
            if (TryCreateInstallation(executable, out var installation))
            {
                return installation;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateInstalledExecutables()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        static IEnumerable<string> EnumerateUnder(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                yield break;
            }

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(root, "gs*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                yield break;
            }

            foreach (var dir in subdirectories.OrderByDescending(ParseVersion))
            {
                var direct = Path.Combine(dir, "gswin64c.exe");
                if (File.Exists(direct))
                {
                    yield return direct;
                }

                var bin = Path.Combine(dir, "bin", "gswin64c.exe");
                if (File.Exists(bin))
                {
                    yield return bin;
                }
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            foreach (var exe in EnumerateUnder(Path.Combine(programFiles, "gs")))
            {
                yield return exe;
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            foreach (var exe in EnumerateUnder(Path.Combine(programFilesX86, "gs")))
            {
                yield return exe;
            }
        }
    }

    private static Version ParseVersion(string path)
    {
        var name = Path.GetFileName(path) ?? string.Empty;
        var digits = Regex.Match(name, "gs(?<version>[0-9][0-9A-Za-z_.-]*)");
        if (!digits.Success)
        {
            return new Version(0, 0);
        }

        var value = digits.Groups["version"].Value.Replace('_', '.');
        return Version.TryParse(value, out var version) ? version : new Version(0, 0);
    }

    private static bool TryCreateInstallation(string executable, out GhostscriptInstallation installation)
    {
        installation = default!;

        if (!File.Exists(executable))
        {
            return false;
        }

        var exeDirectory = Path.GetDirectoryName(executable);
        string? rootDirectory = exeDirectory;

        if (!string.IsNullOrEmpty(exeDirectory))
        {
            var parent = Directory.GetParent(exeDirectory);
            if (parent is not null && string.Equals(Path.GetFileName(exeDirectory), "bin", StringComparison.OrdinalIgnoreCase))
            {
                rootDirectory = parent.FullName;
            }
        }

        var resource = rootDirectory is null
            ? null
            : Path.Combine(rootDirectory, "Resource", "Init");

        if (resource is not null && !Directory.Exists(resource))
        {
            resource = null;
        }

        installation = new GhostscriptInstallation(executable, resource);
        return true;
    }

    private static string? GetBaseDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.CurrentDirectory;
        }

        return baseDir;
    }

}

internal static class DefaultGhostscriptArguments
{
    public static readonly string[] Base =
    {
        "-r150",
        "-dALLOWPSTRANSPARENCY",
        "-dAutoRotatePages=/None",
        "-dBATCH",
        "-dCannotEmbedFontPolicy=/Warning",
        "-dColorConversionStrategy=/LeaveColorUnchanged",
        "-dColorConversionStrategy=/sRGB",
        "-dColorImageDownsampleThreshold=1.0",
        "-dColorImageDownsampleType=/Bicubic",
        "-dColorImageResolution=150",
        "-dCompressFonts=true",
        "-dCompressPages=true",
        "-dCompressStreams=true",
        "-dConvertCMYKImagesToRGB=true",
        "-dConvertImagesToIndexed=false",
        "-dCreateJobTicket=false",
        "-dDetectDuplicateImages=true",
        "-dDoThumbnails=false",
        "-dEmbedAllFonts=true",
        "-dEncodeColorImages=true",
        "-dEncodeGrayImages=true",
        "-dEncodeMonoImages=true",
        "-dFastWebView=false",
        "-dGrayDetection=true",
        "-dGrayImageDownsampleThreshold=1.0",
        "-dGrayImageDownsampleType=/Bicubic",
        "-dGrayImageResolution=150",
        "-dHaveTransparency=true",
        "-dLZWEncodePages=true",
        "-dMaxBitmap=0",
        "-dMonoImageDownsampleThreshold=1.0",
        "-dMonoImageDownsampleType=/Bicubic",
        "-dMonoImageFilter=/CCITTFaxEncode",
        "-dMonoImageResolution=150",
        "-dNOPAUSE",
        "-dNOPROMPT",
        "-dOptimize=false",
        "-dParseDSCComments=false",
        "-dParseDSCCommentsForDocInfo=false",
        "-dPDFNOCIDFALLBACK",
        "-dPDFNOCIDFALLBACK",
        "-dPDFSETTINGS=/screen",
        "-dPreserveAnnots=true",
        "-dPreserveCopyPage=false",
        "-dPreserveDeviceN=false",
        "-dPreserveDeviceN=true",
        "-dPreserveEPSInfo=false",
        "-dPreserveEPSInfo=false",
        "-dPreserveHalftoneInfo=false",
        "-dPreserveOPIComments=false",
        "-dPreserveOverprintSettings=false",
        "-dPreserveOverprintSettings=true",
        "-dPreserveSeparation=false",
        "-dPreserveSeparation=true",
        "-dPrinted=false",
        "-dProcessColorModel=/DeviceRGB",
        "-dSAFER",
        "-dSubsetFonts=true",
        "-dTransferFunctionInfo=/Apply",
        "-dTransferFunctionInfo=/Preserve",
        "-dUCRandBGInfo=/Remove"
    };

    public static readonly string[] Lossy =
    {
        "-dAutoFilterGrayImages=false",
        "-dAutoFilterColorImages=false",
        "-dAutoFilterMonoImages=true",
        "-dColorImageFilter=/DCTEncode",
        "-dDownsampleColorImages=true",
        "-dDownsampleGrayImages=true",
        "-dDownsampleMonoImages=true",
        "-dGrayImageFilter=/DCTEncode",
        "-dPassThroughJPEGImages=false",
        "-dPassThroughJPXImages=false",
        "-dShowAcroForm=false"
    };

    public static readonly string[] Lossless =
    {
        "-dAutoFilterGrayImages=false",
        "-dAutoFilterColorImages=false",
        "-dAutoFilterMonoImages=false",
        "-dColorImageFilter=/DCTEncode",
        "-dDownsampleColorImages=false",
        "-dDownsampleGrayImages=false",
        "-dDownsampleMonoImages=false",
        "-dGrayImageFilter=/DCTEncode",
        "-dPassThroughJPEGImages=true",
        "-dPassThroughJPXImages=true",
        "-dShowAcroForm=true"
    };

    public static readonly string[] MetadataPre =
    {
        "-c",
        "<< /ColorImageDict << /QFactor 0.76 /Blend 1 /HSamples [2 1 1 2] /VSamples [2 1 1 2] >> >> setdistillerparams << /ColorACSImageDict << /QFactor 0.76 /Blend 1 /HSamples [2 1 1 2] /VSamples [2 1 1 2] >> >> setdistillerparams << /GrayImageDict << /QFactor 0.76 /Blend 1 /HSamples [2 1 1 2] /VSamples [2 1 1 2] >> >> setdistillerparams << /GrayACSImageDict << /QFactor 0.76 /Blend 1 /HSamples [2 1 1 2] /VSamples [2 1 1 2] >> >> setdistillerparams << /AlwaysEmbed [ ] >> setdistillerparams << /NeverEmbed [/Courier /Courier-Bold /Courier-Oblique /Courier-BoldOblique /Helvetica /Helvetica-Bold /Helvetica-Oblique /Helvetica-BoldOblique /Times-Roman /Times-Bold /Times-Italic /Times-BoldItalic /Symbol /ZapfDingbats /Arial] >> setdistillerparams",
        "-f",
        "-c",
        "/originalpdfmark { //pdfmark } bind def /pdfmark { { { counttomark pop } stopped { /pdfmark errordict /unmatchedmark get exec stop } if dup type /nametype ne { /pdfmark errordict /typecheck get exec stop } if dup /DOCINFO eq { (Skipping DOCINFO pdfmark\n) print cleartomark exit } if originalpdfmark exit } loop } def",
        "-f"
    };

    public static readonly string[] MetadataPost =
    {
        "-c", "/pdfmark { originalpdfmark } bind def", "-f",
        "-c", "[ /Producer () /ModDate () /CreationDate () /DOCINFO pdfmark", "-f"
    };
}

internal sealed record GhostscriptInstallation(string ExecutablePath, string? ResourceDirectory);

internal static class PdfPresetArguments
{
    public static readonly string[] Text =
    {
        "-dDownsampleColorImages=false",
        "-dDownsampleGrayImages=false",
        "-dDownsampleMonoImages=false",
        "-dColorImageResolution=225",
        "-dGrayImageResolution=225",
        "-dMonoImageResolution=300",
        "-dFastWebView=true",
        "-dDetectDuplicateImages=true",
        "-dColorConversionStrategy=/LeaveColorUnchanged"
    };

    public static readonly string[] Mixed =
    {
        "-dDownsampleColorImages=true",
        "-dDownsampleGrayImages=true",
        "-dDownsampleMonoImages=true",
        "-dColorImageResolution=150",
        "-dGrayImageResolution=150",
        "-dMonoImageResolution=300",
        "-dColorImageDownsampleThreshold=1.1",
        "-dGrayImageDownsampleThreshold=1.1",
        "-dMonoImageDownsampleThreshold=1.1",
        "-dFastWebView=true",
        "-dDetectDuplicateImages=true",
        "-dColorConversionStrategy=/sRGB"
    };

    public static readonly string[] Graphics =
    {
        "-dDownsampleColorImages=true",
        "-dDownsampleGrayImages=true",
        "-dDownsampleMonoImages=true",
        "-dColorImageResolution=110",
        "-dGrayImageResolution=110",
        "-dMonoImageResolution=240",
        "-dColorImageDownsampleThreshold=1.5",
        "-dGrayImageDownsampleThreshold=1.5",
        "-dMonoImageDownsampleThreshold=1.5",
        "-dAutoFilterColorImages=false",
        "-dAutoFilterGrayImages=false",
        "-dColorConversionStrategy=/sRGB",
        "-dDetectDuplicateImages=true"
    };
}
