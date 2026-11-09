using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Shared;

namespace Squish.Core.Documents;

/// <summary>
/// One document run. The package is never edited in place: a new zip is written next to the work files, checked
/// (<see cref="DocumentPackageValidator"/>) and only then offered as a replacement - and only when it is meaningfully smaller.
/// </summary>
internal sealed class DocumentOptimisationJob : IDisposable
{
    private static readonly HashSet<string> OoxmlImageExtensions = new(StringComparer.Ordinal) { "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff" };
    private static readonly HashSet<string> PortableImageExtensions = new(StringComparer.Ordinal) { "png", "jpg", "jpeg", "gif" };

    // Already-compressed payloads gain nothing from the slow deflate level.
    private static readonly HashSet<string> IncompressibleExtensions = new(StringComparer.Ordinal)
    {
        "png", "jpg", "jpeg", "gif", "webp", "avif", "heic", "mp4", "m4v", "mov", "mp3", "m4a", "wmv", "zip", "docx", "xlsx", "pptx", "woff2"
    };

    private static readonly HashSet<string> CoreScrubNames = new(StringComparer.Ordinal) { "creator", "lastModifiedBy", "revision" };
    private static readonly HashSet<string> AppScrubNames = new(StringComparer.Ordinal) { "Company", "Manager" };
    private static readonly HashSet<string> OdfScrubNames = new(StringComparer.Ordinal) { "initial-creator", "creator" };

    private const long InMemoryPartLimit = 16L * 1024 * 1024;

    // A hostile archive can claim gigabytes of uncompressed data per entry; bound what is staged on disk.
    private const long MaxStagedMediaBytes = 1536L * 1024 * 1024;
    private const long MaxRewrittenPartBytes = 1024L * 1024 * 1024;

    private readonly OptimisationRequest _request;
    private readonly DocumentOptimiserOptions _options;
    private readonly OptimiserExecutionContext _context;
    private readonly CancellationToken _token;
    private readonly TempFileScope _temp = new();
    private readonly FilePath _source;
    private readonly bool _aggressive;
    private readonly List<string> _notes = new();
    private readonly HashSet<string> _rewrittenParts = new(StringComparer.Ordinal);

    public DocumentOptimisationJob(OptimisationRequest request, DocumentOptimiserOptions options, OptimiserExecutionContext context, CancellationToken token)
    {
        _request = request;
        _options = options;
        _context = context;
        _token = token;
        _source = request.SourcePath;
        _aggressive = OptimisationMetadata.GetFlag(request.Metadata, OptimisationMetadata.DocumentAggressive);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>What happened to one embedded picture.</summary>
    private sealed record MediaOutcome(string NewEntryName, FilePath File, long NewSize);

    public async Task<OptimisationResult> RunAsync()
    {
        var started = DateTime.UtcNow;
        _context.ReportProgress(3, "Inspecting document");

        var sniff = DocumentSniffer.Sniff(_source.Value);
        if (!sniff.IsSupported)
        {
            return Untouched(started, sniff.Outcome switch
            {
                DocumentSniffOutcome.Protected => $"{sniff.Detail}; left untouched",
                DocumentSniffOutcome.LegacyOrEncrypted => $"{sniff.Detail}; left untouched",
                DocumentSniffOutcome.Malformed => $"Unreadable archive ({sniff.Detail}); left untouched",
                _ => "Not a supported document; left untouched"
            });
        }

        if (DocumentSniffer.IsOpenInAnApplication(_source.Value))
        {
            return Untouched(started, "Document is open in an application; left untouched");
        }

        var originalSize = new FileInfo(_source.Value).Length;
        if (originalSize > _options.MaxInputBytes)
        {
            return Untouched(started, "Document is too large to optimise safely");
        }

        var format = sniff.Format;
        var output = _temp.New("squish-doc", _source.Extension ?? "zip");
        HashSet<string> sourceDangling;
        var rewrittenParts = 0;

        using (var sourceStream = new FileStream(_source.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var archive = new ZipArchive(sourceStream, ZipArchiveMode.Read))
        {
            sourceDangling = format == DocumentFormat.Ooxml ? DocumentPackageValidator.FindDangling(archive) : new HashSet<string>(StringComparer.Ordinal);

            var names = new HashSet<string>(archive.Entries.Select(e => e.FullName), StringComparer.Ordinal);
            var removed = PlanRemovals(format, names);

            _context.ReportProgress(8, "Optimising embedded pictures");
            var media = _options.OptimiseImages
                ? await OptimiseMediaAsync(archive, format, names).ConfigureAwait(false)
                : new Dictionary<string, MediaOutcome>(StringComparer.Ordinal);

            _context.ReportProgress(70, "Repacking document");
            rewrittenParts = WritePackage(archive, format, names, removed, media, output);
        }

        _context.ReportProgress(90, "Checking result");
        var problem = DocumentPackageValidator.Validate(output.Value, format, sourceDangling, _rewrittenParts);
        if (problem is not null)
        {
            Log.Warning($"Document result rejected for {_source.Value}: {problem}");
            return Untouched(started, "Could not rewrite safely; original kept");
        }

        var newSize = new FileInfo(output.Value).Length;
        var minimumSaving = originalSize * (_options.MinimumSavingsPercent / 100.0);
        if (newSize >= originalSize - minimumSaving)
        {
            _context.ReportProgress(95, "No size improvement; keeping original");
            return Untouched(started, "Original already optimal");
        }

        var plan = OptimisedOutputPlanner.Plan(_source, _source.Extension ?? "zip", _request.Metadata, BuildOutputPath);
        var destination = plan.Destination;
        destination.EnsureParentDirectoryExists();

        var times = _options.PreserveDates ? ReadTimestamps(_source) : null;
        File.Copy(output.Value, destination.Value, overwrite: true);
        if (times is { } t)
        {
            RestoreTimestamps(destination, t);
        }

        var message = $"{Describe()}. Saved {(originalSize - newSize).HumanSize()} ({originalSize.HumanSize()} → {newSize.HumanSize()})";
        _context.ReportProgress(100, message);
        return new OptimisationResult(_request.RequestId, OptimisationStatus.Succeeded, destination, message, DateTime.UtcNow - started);
    }

    private string Describe() => _notes.Count == 0 ? "Repacked document" : string.Join(", ", _notes);

    private OptimisationResult Untouched(DateTime started, string reason)
    {
        _context.ReportProgress(100, reason);
        return new OptimisationResult(_request.RequestId, OptimisationStatus.Succeeded, _source, reason, DateTime.UtcNow - started);
    }

    // ------------------------------------------------------------------------------------------------------
    // Planning
    // ------------------------------------------------------------------------------------------------------

    private HashSet<string> PlanRemovals(DocumentFormat format, HashSet<string> names)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        if (format == DocumentFormat.Ooxml)
        {
            if (names.Contains("xl/calcChain.xml"))
            {
                // Excel rebuilds the calculation chain on load; it can be several megabytes in big models.
                removed.Add("xl/calcChain.xml");
                _notes.Add("dropped calcChain");
            }

            if (_options.StripMetadata)
            {
                foreach (var name in names.Where(n => n.StartsWith("docProps/thumbnail.", StringComparison.OrdinalIgnoreCase)))
                {
                    removed.Add(name);
                }
            }
        }
        else if (format == DocumentFormat.OpenDocument && _options.StripMetadata)
        {
            foreach (var name in names.Where(n => n.StartsWith("Thumbnails/", StringComparison.Ordinal)))
            {
                removed.Add(name);
            }
        }

        return removed;
    }

    // ------------------------------------------------------------------------------------------------------
    // Embedded pictures
    // ------------------------------------------------------------------------------------------------------

    private static bool IsMediaEntry(DocumentFormat format, string name)
    {
        var extension = PackagePaths.ExtensionOf(name);
        return format switch
        {
            DocumentFormat.Ooxml => OoxmlImageExtensions.Contains(extension)
                && (name.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("ppt/media/", StringComparison.OrdinalIgnoreCase)),
            DocumentFormat.OpenDocument => PortableImageExtensions.Contains(extension) && name.StartsWith("Pictures/", StringComparison.Ordinal),
            DocumentFormat.Epub => PortableImageExtensions.Contains(extension),
            _ => false
        };
    }

    private async Task<Dictionary<string, MediaOutcome>> OptimiseMediaAsync(ZipArchive archive, DocumentFormat format, HashSet<string> names)
    {
        var outcomes = new Dictionary<string, MediaOutcome>(StringComparer.Ordinal);
        var candidates = archive.Entries
            .Where(e => IsMediaEntry(format, e.FullName) && e.Length >= _options.MinimumImageBytes && e.Length <= 256L * 1024 * 1024)
            .ToList();
        if (candidates.Count == 0)
        {
            return outcomes;
        }

        // Stage each picture once; identical bytes (very common: the same logo on every slide) share one optimisation run.
        var groups = new Dictionary<string, List<(ZipArchiveEntry Entry, FilePath Staged)>>(StringComparer.Ordinal);
        var stagedBytes = 0L;
        foreach (var entry in candidates)
        {
            _token.ThrowIfCancellationRequested();
            stagedBytes += entry.Length;
            if (stagedBytes > MaxStagedMediaBytes)
            {
                _notes.Add("skipped some pictures (too much embedded media)");
                break;
            }

            var staged = _temp.New("squish-docimg", CanonicalExtension(PackagePaths.ExtensionOf(entry.FullName)));
            string hash;
            using (var input = entry.Open())
            using (var file = new FileStream(staged.Value, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, _token).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), _token).ConfigureAwait(false);
                }

                hash = Convert.ToHexString(sha.GetHashAndReset());
            }

            if (!groups.TryGetValue(hash, out var members))
            {
                members = new List<(ZipArchiveEntry, FilePath)>();
                groups[hash] = members;
            }
            else
            {
                _temp.Release(staged);
            }

            members.Add((entry, staged));
        }

        Dictionary<string, ImageDisplayNeed> needs = new(StringComparer.Ordinal);
        if (_aggressive && format == DocumentFormat.Ooxml)
        {
            needs = OoxmlImageUsage.Analyse(archive, new HashSet<string>(candidates.Select(c => c.FullName), StringComparer.Ordinal));
        }

        var usedNames = new HashSet<string>(names, StringComparer.Ordinal);
        var done = 0;
        var imageSaved = 0L;
        foreach (var members in groups.Values)
        {
            _token.ThrowIfCancellationRequested();
            var stagedFile = members[0].Staged;
            var originalLength = new FileInfo(stagedFile.Value).Length;
            var originalExtension = PackagePaths.ExtensionOf(members[0].Entry.FullName);

            var longEdge = _aggressive && format == DocumentFormat.Ooxml
                ? TargetLongEdge(stagedFile, members.Select(m => m.Entry.FullName), needs)
                : 0;

            var result = await RunImagePipelineAsync(stagedFile, format, longEdge).ConfigureAwait(false);
            done++;
            _context.ReportProgress(8 + 60.0 * done / groups.Count, $"Optimised picture {done} of {groups.Count}");
            if (result is null)
            {
                continue;
            }

            var outputFile = result.Value;
            var newLength = new FileInfo(outputFile.Value).Length;
            if (newLength >= originalLength)
            {
                continue;
            }

            var newExtension = (outputFile.Extension ?? originalExtension).ToLowerInvariant();
            var sameFormat = SameImageFormat(originalExtension, newExtension);
            if (!sameFormat && format != DocumentFormat.Ooxml)
            {
                // ODF and EPUB reference pictures from content that is not rewritten here, so the file type must not change.
                continue;
            }

            foreach (var (entry, _) in members)
            {
                var newName = entry.FullName;
                if (!sameFormat)
                {
                    newName = UniqueName(PackagePaths.WithExtension(entry.FullName, newExtension), usedNames);
                }

                outcomes[entry.FullName] = new MediaOutcome(newName, outputFile, newLength);
                imageSaved += originalLength - newLength;
            }
        }

        if (outcomes.Count > 0)
        {
            _notes.Add($"re-encoded {outcomes.Count} picture{(outcomes.Count == 1 ? string.Empty : "s")} (saved {imageSaved.HumanSize()})");
            if (outcomes.Any(o => o.Key != o.Value.NewEntryName))
            {
                _notes.Add("converted formats");
            }
        }

        return outcomes;
    }

    /// <summary>Maps to a literal so no archive-supplied text ever reaches a file system path.</summary>
    private static string CanonicalExtension(string extension) => extension switch
    {
        "png" => "png",
        "jpg" => "jpg",
        "jpeg" => "jpeg",
        "gif" => "gif",
        "bmp" => "bmp",
        "tif" => "tif",
        "tiff" => "tiff",
        _ => "bin"
    };

    private static bool SameImageFormat(string a, string b)
        => string.Equals(a, b, StringComparison.Ordinal)
           || (a is "jpg" or "jpeg" && b is "jpg" or "jpeg")
           || (a is "tif" or "tiff" && b is "tif" or "tiff");

    private static string UniqueName(string wanted, HashSet<string> used)
    {
        var candidate = wanted;
        var counter = 1;
        while (!used.Add(candidate))
        {
            var dot = wanted.LastIndexOf('.');
            candidate = $"{wanted[..dot]}-sq{counter++}{wanted[dot..]}";
        }

        return candidate;
    }

    /// <summary>Longest edge to downscale to (0 = leave the resolution alone), from how large the picture is actually drawn.</summary>
    private int TargetLongEdge(FilePath staged, IEnumerable<string> entryNames, Dictionary<string, ImageDisplayNeed> needs)
    {
        double needWidth = 0, needHeight = 0;
        foreach (var name in entryNames)
        {
            if (!needs.TryGetValue(name, out var need))
            {
                return 0; // one unknown use keeps full resolution
            }

            needWidth = Math.Max(needWidth, need.WidthInches);
            needHeight = Math.Max(needHeight, need.HeightInches);
        }

        var probe = ImageIO.Probe(staged.Value);
        if (probe is null || probe.Width <= 0 || probe.Height <= 0)
        {
            return 0;
        }

        var dpi = Math.Max(72, _options.AggressiveTargetDpi);
        var scale = Math.Max(needWidth * dpi / probe.Width, needHeight * dpi / probe.Height);

        // Only worth a lossy resample when the picture is shown at well under its pixel density.
        if (scale > 0.67)
        {
            return 0;
        }

        var longEdge = (int)Math.Ceiling(Math.Max(probe.Width, probe.Height) * scale);
        return Math.Max(longEdge, 640);
    }

    private async Task<FilePath?> RunImagePipelineAsync(FilePath staged, DocumentFormat format, int longEdge)
    {
        var baseline = _options.ImageOptions;
        var convertToJpeg = _aggressive && format == DocumentFormat.Ooxml
            ? new HashSet<string>(["png", "bmp", "tif", "tiff"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var imageOptions = baseline with
        {
            FormatsToConvertToJpeg = convertToJpeg,
            FormatsToConvertToPng = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DownscaleRetina = longEdge > 0,
            RetinaLongEdgePixels = longEdge > 0 ? longEdge : baseline.RetinaLongEdgePixels,
            PreserveDates = false,
            RequireSizeImprovement = true,
            AdvancedCodecs = baseline.AdvancedCodecs with { AllowWebpOutput = false, AllowAvifOutput = false }
        };

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [OptimisationMetadata.OutputReplaceOriginal] = true,
            [OptimisationMetadata.ImageAggressive] = _aggressive
        };

        var request = new OptimisationRequest(ItemType.Image, staged, metadata: metadata);
        var context = new OptimiserExecutionContext(request.RequestId, static _ => { });
        var result = await new ImageOptimiser(imageOptions).OptimiseAsync(request, context, _token).ConfigureAwait(false);
        if (result.Status != OptimisationStatus.Succeeded || result.OutputPath is not { } outputPath || !File.Exists(outputPath.Value))
        {
            return null;
        }

        if (!string.Equals(outputPath.Value, staged.Value, StringComparison.OrdinalIgnoreCase))
        {
            _temp.Track(outputPath);
        }

        return outputPath;
    }

    // ------------------------------------------------------------------------------------------------------
    // Writing the package
    // ------------------------------------------------------------------------------------------------------

    private int WritePackage(
        ZipArchive archive,
        DocumentFormat format,
        HashSet<string> names,
        HashSet<string> removed,
        Dictionary<string, MediaOutcome> media,
        FilePath outputPath)
    {
        var renamed = media.Where(m => m.Key != m.Value.NewEntryName).ToDictionary(m => m.Key, m => m.Value.NewEntryName, StringComparer.Ordinal);
        var missingDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format == DocumentFormat.Ooxml && renamed.Count > 0 && archive.GetEntry(PackagePaths.ContentTypesPart) is { } contentTypesEntry)
        {
            using var stream = contentTypesEntry.Open();
            var (defaults, _) = ContentTypesRewriter.Read(stream);
            foreach (var newName in renamed.Values)
            {
                var extension = PackagePaths.ExtensionOf(newName);
                if (!defaults.Contains(extension))
                {
                    missingDefaults[extension] = PackagePaths.ContentTypeForImageExtension(extension);
                }
            }
        }

        SpreadsheetStyles? styles = null;
        var rewritten = 0;
        var cleanedSheets = 0;
        _rewrittenParts.Clear();
        var strippedMetadata = false;

        using var outputStream = new FileStream(outputPath.Value, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var entry in archive.Entries.ToList())
        {
            _token.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (removed.Contains(name))
            {
                continue;
            }

            if (media.TryGetValue(name, out var replacement))
            {
                WriteFile(output, replacement.NewEntryName, replacement.File, entry.LastWriteTime);
                continue;
            }

            if (name.EndsWith('/'))
            {
                output.CreateEntry(name).LastWriteTime = entry.LastWriteTime;
                continue;
            }

            if (name == "mimetype")
            {
                CopyRaw(entry, output, name, CompressionLevel.NoCompression);
                continue;
            }

            var level = LevelFor(name);
            if (entry.Length > MaxRewrittenPartBytes)
            {
                CopyRaw(entry, output, name, level);
                continue;
            }

            var transform = SelectTransform(format, entry, removed, renamed, missingDefaults, ref styles, archive, out var isSheet);
            if (transform is null)
            {
                CopyRaw(entry, output, name, level);
                continue;
            }

            if (TryTransform(entry, output, level, transform))
            {
                rewritten++;
                _rewrittenParts.Add(name);
                if (isSheet)
                {
                    cleanedSheets++;
                }

                if (_options.StripMetadata && IsMetadataPart(format, name))
                {
                    strippedMetadata = true;
                }
            }
            else
            {
                CopyRaw(entry, output, name, level);
            }
        }

        if (cleanedSheets > 0)
        {
            _notes.Add($"cleaned {cleanedSheets} worksheet{(cleanedSheets == 1 ? string.Empty : "s")}");
        }

        if (strippedMetadata)
        {
            _notes.Add("scrubbed document properties");
        }

        return rewritten;
    }

    private static bool IsMetadataPart(DocumentFormat format, string name)
        => format == DocumentFormat.Ooxml
            ? name is "docProps/core.xml" or "docProps/app.xml"
            : format == DocumentFormat.OpenDocument && name == "meta.xml";

    private static CompressionLevel LevelFor(string name)
        => IncompressibleExtensions.Contains(PackagePaths.ExtensionOf(name)) ? CompressionLevel.Fastest : CompressionLevel.SmallestSize;

    /// <summary>
    /// Returns the rewrite to apply to a part, or null to copy it byte for byte. <paramref name="open"/> reopens the source
    /// entry because the worksheet cleaner reads it more than once.
    /// </summary>
    private Action<Func<Stream>, Stream>? SelectTransform(
        DocumentFormat format,
        ZipArchiveEntry entry,
        HashSet<string> removed,
        Dictionary<string, string> renamed,
        Dictionary<string, string> missingDefaults,
        ref SpreadsheetStyles? styles,
        ZipArchive archive,
        out bool isSheet)
    {
        isSheet = false;
        var name = entry.FullName;

        switch (format)
        {
            case DocumentFormat.Ooxml:
                if (name == PackagePaths.ContentTypesPart)
                {
                    return (open, dest) => { using var s = open(); new ContentTypesRewriter(removed, missingDefaults).Rewrite(s, dest); };
                }

                if (PackagePaths.IsRelationshipsPart(name))
                {
                    return (open, dest) => { using var s = open(); new RelationshipsRewriter(name, removed, renamed).Rewrite(s, dest); };
                }

                if (_options.StripMetadata && name == "docProps/core.xml")
                {
                    return (open, dest) => { using var s = open(); new ElementRemovingRewriter(CoreScrubNames).Rewrite(s, dest); };
                }

                if (_options.StripMetadata && name == "docProps/app.xml")
                {
                    return (open, dest) => { using var s = open(); new ElementRemovingRewriter(AppScrubNames).Rewrite(s, dest); };
                }

                if (name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && name.IndexOf('/', "xl/worksheets/".Length) < 0)
                {
                    isSheet = true;
                    styles ??= LoadStyles(archive);
                    var captured = styles;
                    return (open, dest) => CleanSheet(open, dest, captured);
                }

                if ((name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) && !name.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase))
                {
                    return (open, dest) => { using var s = open(); new XmlPartRewriter().Rewrite(s, dest); };
                }

                return null;

            case DocumentFormat.OpenDocument:
                if (name == "META-INF/manifest.xml")
                {
                    var prefixes = removed.Count > 0 && _options.StripMetadata ? new[] { "Thumbnails/" } : Array.Empty<string>();
                    return (open, dest) => { using var s = open(); new OdfManifestRewriter(prefixes).Rewrite(s, dest); };
                }

                if (name == "meta.xml" && _options.StripMetadata)
                {
                    return (open, dest) => { using var s = open(); new ElementRemovingRewriter(OdfScrubNames).Rewrite(s, dest); };
                }

                if (name is "meta.xml" or "settings.xml")
                {
                    return (open, dest) => { using var s = open(); new XmlPartRewriter().Rewrite(s, dest); };
                }

                return null;

            case DocumentFormat.Epub:
                if (name == "META-INF/container.xml" || name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                {
                    return (open, dest) => { using var s = open(); new XmlPartRewriter().Rewrite(s, dest); };
                }

                return null;

            default:
                return null;
        }
    }

    private static SpreadsheetStyles LoadStyles(ZipArchive archive)
    {
        if (archive.GetEntry("xl/styles.xml") is not { } stylesEntry)
        {
            return SpreadsheetStyles.Unknown;
        }

        try
        {
            using var stream = stylesEntry.Open();
            return SpreadsheetStyles.Parse(stream);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            return SpreadsheetStyles.Unknown;
        }
    }

    private void CleanSheet(Func<Stream> open, Stream destination, SpreadsheetStyles styles)
    {
        var cutoff = int.MaxValue;
        if (_aggressive)
        {
            using var scanStream = open();
            var scan = SpreadsheetCleaner.Scan(scanStream);
            if (scan.HasImplicitReferences)
            {
                using var plain = open();
                new XmlPartRewriter().Rewrite(plain, destination);
                return;
            }

            cutoff = Math.Max(scan.LastContentRow, scan.LastMergedRow);
        }

        // First a dry run to learn what survives (and so what the dimension must say), then the real pass.
        var dry = new SpreadsheetCleaner(styles, _aggressive, cutoff, null);
        using (var dryStream = open())
        {
            dry.Rewrite(dryStream, Stream.Null);
        }

        if (!dry.Stats.AnyDropped)
        {
            using var plain = open();
            new XmlPartRewriter().Rewrite(plain, destination);
            return;
        }

        var cleaner = new SpreadsheetCleaner(styles, _aggressive, cutoff, dry.Stats.DimensionRef);
        using var input = open();
        cleaner.Rewrite(input, destination);
    }

    /// <summary>Runs a rewrite into a buffer first so a part that fails half way can still be copied untouched.</summary>
    private bool TryTransform(ZipArchiveEntry source, ZipArchive output, CompressionLevel level, Action<Func<Stream>, Stream> transform)
    {
        using var buffer = CreateBuffer(source.Length);
        try
        {
            transform(source.Open, buffer);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Log.Warning($"Kept {source.FullName} unchanged: {ex.Message}");
            return false;
        }

        buffer.Position = 0;
        var created = output.CreateEntry(source.FullName, level);
        created.LastWriteTime = source.LastWriteTime;
        using var destination = created.Open();
        buffer.CopyTo(destination);
        return true;
    }

    private Stream CreateBuffer(long hint)
    {
        if (hint <= InMemoryPartLimit)
        {
            return new MemoryStream();
        }

        var file = _temp.New("squish-docpart", "tmp");
        return new FileStream(file.Value, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    private static void CopyRaw(ZipArchiveEntry source, ZipArchive output, string name, CompressionLevel level)
    {
        var created = output.CreateEntry(name, level);
        created.LastWriteTime = source.LastWriteTime;
        using var input = source.Open();
        using var destination = created.Open();
        input.CopyTo(destination);
    }

    // The analyzer follows MediaOutcome as a whole and sees the entry name beside the path. The path is a work file this job
    // created; entry names are only ever used as names inside the output archive, never as file system paths.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5389:Do not add archive item's path to the target file system path", Justification = "file is a temp file created by this job.")]
    private static void WriteFile(ZipArchive output, string name, FilePath file, DateTimeOffset timestamp)
    {
        var created = output.CreateEntry(name, LevelFor(name));
        created.LastWriteTime = timestamp;
        using var input = new FileStream(file.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = created.Open();
        input.CopyTo(destination);
    }

    // ------------------------------------------------------------------------------------------------------
    // Output
    // ------------------------------------------------------------------------------------------------------

    private static FilePath BuildOutputPath(FilePath sourcePath, string extension)
    {
        var stem = sourcePath.Stem;
        const string suffix = ".squish";
        if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^suffix.Length];
        }

        return sourcePath.Parent.Append($"{stem}.squish.{extension}");
    }

    private static (DateTime Created, DateTime Modified)? ReadTimestamps(FilePath path)
    {
        try
        {
            return (File.GetCreationTimeUtc(path.Value), File.GetLastWriteTimeUtc(path.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RestoreTimestamps(FilePath path, (DateTime Created, DateTime Modified) times)
    {
        try
        {
            File.SetCreationTimeUtc(path.Value, times.Created);
            File.SetLastWriteTimeUtc(path.Value, times.Modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Timestamps are a nicety; they must not fail a finished optimisation.
        }
    }
}
