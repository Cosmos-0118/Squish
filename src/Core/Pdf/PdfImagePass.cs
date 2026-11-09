using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Imaging;
using Squish.Core.Optimizers;
using Squish.Core.Processes;
using Squish.Core.Shared;
using ImageMagick;

namespace Squish.Core.Pdf;

/// <param name="Output">The rewritten file, or null when nothing needed to change.</param>
internal sealed record PdfImagePassResult(int ImagesChanged, long BytesSaved, string? Output);

/// <summary>
/// Rewrites the embedded pictures of a PDF through qpdf's JSON interface, leaving every other object alone:
/// <list type="bullet">
/// <item>DCT (JPEG) pictures: lossless Huffman/progressive recompression, then a perceptually guarded re-encode, downsampled when
/// the picture is drawn at a much higher resolution than the target.</item>
/// <item>8-bit RGB/Gray Flate pictures: converted to JPEG when the content is photographic and the guard accepts it; otherwise
/// (screenshots, charts, logos) re-encoded as an optimised PNG whose IDAT stream is carried into the PDF as-is
/// (<c>/FlateDecode</c> with <c>/Predictor 15</c>, or an <c>/Indexed</c> palette after quantisation).</item>
/// <item>8-bit soft masks: shrunk with their picture and stored with PNG row filters, still lossless.</item>
/// <item>Everything else (1-bit scans, CCITT, JBIG2, JPX, CMYK, indexed, masks with colour keys, inverted /Decode) is untouched.</item>
/// </list>
/// Identical pictures (same bytes, same dictionary, same placement size) are optimised once and share the result.
/// </summary>
internal sealed class PdfImagePass
{
    private const int ExportBatchSize = 64;
    private const int MaxTrackedStreams = 6000;
    private const int MaxFormDepth = 6;
    private const long MaxImageBytes = 256L * 1024 * 1024;

    private readonly QpdfRunner _qpdf;
    private readonly PdfObjectMap _structure;
    private readonly PdfOptimiserOptions _options;
    private readonly bool _aggressive;
    private readonly string _workDirectory;
    private readonly OptimiserExecutionContext _context;
    private readonly CancellationToken _token;
    private readonly TempFileScope _temp = new();
    private readonly ImageEncoders _encoders;

    private string _inputPdf = string.Empty;
    private HashSet<int> _reachable = new();
    private readonly HashSet<int> _maskImages = new();
    private readonly Dictionary<int, List<int>> _softMaskParents = new();
    private readonly Dictionary<int, string> _exportedFiles = new();
    private readonly Dictionary<int, JsonElement> _exportedDicts = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageEncoding?>>> _shared = new(StringComparer.Ordinal);

    public PdfImagePass(QpdfRunner qpdf, PdfObjectMap structure, PdfOptimiserOptions options, bool aggressive, string workDirectory, OptimiserExecutionContext context, CancellationToken token)
    {
        _qpdf = qpdf;
        _structure = structure;
        _options = options;
        _aggressive = aggressive;
        _workDirectory = workDirectory;
        _context = context;
        _token = token;
        _encoders = new ImageEncoders(ImageToolset.Resolve(options.ImageOptions.AdvancedCodecs), _temp);
    }

    private int TargetDpi => _aggressive ? _options.AggressiveTargetDpi : _options.TargetDpi;

    private int Workers => Math.Clamp(Environment.ProcessorCount / 2, 1, 3);

    // Several workers help with many small pictures; a few big scans must not be decoded side by side (about 1 GB each).
    // Process-wide: several PDFs (and Office files) optimised at once share one allowance instead of each taking 12 MP.
    private readonly PixelBudget _budget = PixelBudget.Shared;

    /// <param name="inputPdf">The file <see cref="PdfObjectMap"/> was exported from; the update is applied to it directly.</param>
    /// <param name="extraArguments">Transformations every rewrite shares (metadata removal).</param>
    /// <param name="predictorsInSource">Whether the source already has predictor-coded streams.</param>
    public async Task<PdfImagePassResult> RunAsync(string inputPdf, string outputBase, bool linearize, IReadOnlyList<string> extraArguments, bool predictorsInSource)
    {
        try
        {
            return await RunCoreAsync(inputPdf, outputBase, linearize, extraArguments, predictorsInSource).ConfigureAwait(false);
        }
        finally
        {
            _temp.Dispose();
        }
    }

    private async Task<PdfImagePassResult> RunCoreAsync(string inputPdf, string outputBase, bool linearize, IReadOnlyList<string> extraArguments, bool predictorsInSource)
    {
        _inputPdf = inputPdf;
        _reachable = _structure.ReachableIds();

        var images = _reachable.Where(IsImage).OrderBy(id => id).ToList();
        CollectMaskImages(images);
        var updates = new JsonObject();
        var results = new ConcurrentDictionary<int, ImageUpdate>();

        if (images.Count > 0 && _options.OptimiseImages)
        {
            var usage = await FindImageUsageAsync(images).ConfigureAwait(false);
            // Masks are decoded by the mask pass, once; exporting them here as well would only decode them twice.
            await OptimiseImagesAsync(images.Where(id => !_maskImages.Contains(id)).ToList(), usage, results).ConfigureAwait(false);
            await OptimiseMasksAsync(results).ConfigureAwait(false);
        }

        var changed = 0;
        long saved = 0;
        var injectedPredictors = false;
        foreach (var (id, update) in results.OrderBy(r => r.Key))
        {
            updates[$"obj:{id} 0 R"] = update.Node;
            changed++;
            saved += update.Saved;
            injectedPredictors |= update.UsesPredictor;
        }

        if (_options.StripMetadata)
        {
            foreach (var (key, node) in BuildMetadataUpdates())
            {
                if (!updates.ContainsKey(key))
                {
                    updates[key] = node;
                }
            }
        }

        if (updates.Count == 0)
        {
            return new PdfImagePassResult(0, 0, null);
        }

        var header = new JsonObject
        {
            ["jsonversion"] = 2,
            ["pushedinheritedpageresources"] = false,
            ["calledgetallpages"] = false
        };
        var document = new JsonObject { ["qpdf"] = new JsonArray(header, updates) };
        var updatePath = Path.Combine(_workDirectory, "update.json");
        await File.WriteAllTextAsync(updatePath, document.ToJsonString(), _token).ConfigureAwait(false);

        var arguments = new List<string> { $"--update-from-json={updatePath}" };
        arguments.AddRange(extraArguments);
        var output = await _qpdf.WriteBestAsync(inputPdf, outputBase, arguments, linearize, predictorsInSource || injectedPredictors, _token).ConfigureAwait(false);
        if (output is null)
        {
            Log.Warning("qpdf could not apply the picture updates");
            return new PdfImagePassResult(0, 0, null);
        }

        return new PdfImagePassResult(changed, saved, output);
    }

    // ------------------------------------------------------------------------------------------------------
    // Where is each picture drawn, and how large?
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Soft masks and stencil masks carry alpha: JPEG ringing on their edges would show as halos, so they stay lossless.</summary>
    private void CollectMaskImages(List<int> images)
    {
        foreach (var id in images)
        {
            if (_structure.ValueOf(id) is not { } dict)
            {
                continue;
            }

            foreach (var key in new[] { "/SMask", "/Mask" })
            {
                if (dict.TryGetProperty(key, out var target) && PdfObjectMap.TryGetRef(target, out var maskId))
                {
                    _maskImages.Add(maskId);
                    if (key == "/SMask")
                    {
                        if (!_softMaskParents.TryGetValue(maskId, out var parents))
                        {
                            parents = new List<int>();
                            _softMaskParents[maskId] = parents;
                        }

                        parents.Add(id);
                    }
                }
            }
        }
    }

    private bool IsImage(int id)
        => _structure.IsStream(id) && _structure.ValueOf(id) is { } dict && PdfObjectMap.Name(dict, "/Subtype") == "/Image";

    private sealed class ImageUsage
    {
        public readonly List<(double WidthPts, double HeightPts)> Placements = new();
        public bool Unknown;
    }

    /// <summary>
    /// Placements per image object. An image used anywhere this scan did not see (annotation appearances, patterns, parse
    /// trouble) is marked unknown and never downsampled.
    /// </summary>
    private async Task<Dictionary<int, ImageUsage>> FindImageUsageAsync(List<int> images)
    {
        var usage = images.ToDictionary(id => id, _ => new ImageUsage());
        void MarkAllUnknown()
        {
            foreach (var u in usage.Values)
            {
                u.Unknown = true;
            }
        }

        try
        {
            var pages = CollectPages();
            var forms = _reachable.Where(id => _structure.IsStream(id) && _structure.ValueOf(id) is { } d && PdfObjectMap.Name(d, "/Subtype") == "/Form").ToList();
            var streamIds = new HashSet<int>(forms);
            foreach (var page in pages)
            {
                foreach (var content in page.ContentIds)
                {
                    streamIds.Add(content);
                }
            }

            if (streamIds.Count > MaxTrackedStreams || pages.Count == 0)
            {
                MarkAllUnknown();
                return usage;
            }

            if (streamIds.Count > 0)
            {
                // One qpdf run for every content stream and form: the selectors go through an argument file.
                await ExportAsync(streamIds.ToArray(), "content").ConfigureAwait(false);
            }

            var visitedOwners = new HashSet<int>();
            foreach (var page in pages)
            {
                var stream = new MemoryStream();
                foreach (var id in page.ContentIds)
                {
                    var bytes = ReadExported(id);
                    stream.Write(bytes);
                    stream.WriteByte((byte)'\n');
                }

                visitedOwners.Add(page.ResourceOwner);
                // /UserUnit scales default user space (1/72 inch by default): a placement of 100 units is 100 * UserUnit points.
                var start = new PdfMatrix(page.UserUnit, 0, 0, page.UserUnit, 0, 0);
                ScanContent(stream.ToArray(), start, page.Resources, page.ResourceOwner, usage, visitedOwners, 0, new HashSet<int>());
            }

            // The inflated content streams are only needed for the scan; do not keep them on disk through the image passes.
            ForgetExports(streamIds);

            // Anything that references an image from a resource dictionary this scan never walked makes its size unknowable.
            foreach (var (owner, target) in FindResourceReferences())
            {
                if (usage.TryGetValue(target, out var u) && !visitedOwners.Contains(owner))
                {
                    u.Unknown = true;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            Log.Warning($"PDF placement scan failed; pictures keep their resolution: {ex.Message}");
            MarkAllUnknown();
        }

        return usage;
    }

    private sealed record PageInfo(List<int> ContentIds, JsonElement? Resources, int ResourceOwner, double UserUnit);

    private List<PageInfo> CollectPages()
    {
        var pages = new List<PageInfo>();
        if (_structure.Trailer is not { } trailer
            || !trailer.TryGetProperty("value", out var trailerValue)
            || !trailerValue.TryGetProperty("/Root", out var rootRef)
            || !PdfObjectMap.TryGetRef(rootRef, out var rootId)
            || _structure.ValueOf(rootId) is not { } catalog
            || !catalog.TryGetProperty("/Pages", out var pagesRef)
            || !PdfObjectMap.TryGetRef(pagesRef, out var pagesId))
        {
            return pages;
        }

        var seen = new HashSet<int>();
        void Walk(int nodeId, JsonElement? inheritedResources, int inheritedOwner)
        {
            if (!seen.Add(nodeId) || _structure.ValueOf(nodeId) is not { } node)
            {
                return;
            }

            var resources = inheritedResources;
            var owner = inheritedOwner;
            if (node.TryGetProperty("/Resources", out var resourceElement))
            {
                if (PdfObjectMap.TryGetRef(resourceElement, out var resourceId))
                {
                    resources = _structure.ValueOf(resourceId);
                    owner = resourceId;
                }
                else
                {
                    resources = resourceElement;
                    owner = nodeId;
                }
            }

            if (node.TryGetProperty("/Kids", out var kids) && kids.ValueKind == JsonValueKind.Array)
            {
                foreach (var kid in kids.EnumerateArray())
                {
                    if (PdfObjectMap.TryGetRef(kid, out var kidId))
                    {
                        Walk(kidId, resources, owner);
                    }
                }

                return;
            }

            var contents = new List<int>();
            if (node.TryGetProperty("/Contents", out var contentElement))
            {
                CollectContentIds(contentElement, contents);
            }

            var userUnit = PdfObjectMap.Number(node, "/UserUnit") is { } unit && unit > 0 && double.IsFinite(unit) ? unit : 1.0;
            pages.Add(new PageInfo(contents, resources, owner, userUnit));
        }

        Walk(pagesId, null, pagesId);
        return pages;
    }

    private void CollectContentIds(JsonElement element, List<int> ids)
    {
        if (PdfObjectMap.TryGetRef(element, out var id))
        {
            if (_structure.IsStream(id))
            {
                ids.Add(id);
            }
            else if (_structure.ValueOf(id) is { ValueKind: JsonValueKind.Array } array)
            {
                CollectContentIds(array, ids);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectContentIds(item, ids);
            }
        }
    }

    private void ScanContent(byte[] content, PdfMatrix start, JsonElement? resources, int owner, Dictionary<int, ImageUsage> usage, HashSet<int> visitedOwners, int depth, HashSet<int> activeForms)
    {
        if (depth > MaxFormDepth)
        {
            throw new FormatException("Forms nested too deeply.");
        }

        JsonElement? xobjects = null;
        if (resources is { ValueKind: JsonValueKind.Object } res && res.TryGetProperty("/XObject", out var xo))
        {
            xobjects = _structure.Deref(xo);
        }

        PdfContentScanner.Scan(content, start, (name, ctm) =>
        {
            if (xobjects is not { ValueKind: JsonValueKind.Object } table
                || !table.TryGetProperty("/" + name, out var target)
                || !PdfObjectMap.TryGetRef(target, out var targetId))
            {
                return;
            }

            if (usage.TryGetValue(targetId, out var imageUsage))
            {
                var (width, height) = ctm.UnitSquareSize;
                imageUsage.Placements.Add((width, height));
                return;
            }

            if (!_structure.IsStream(targetId) || _structure.ValueOf(targetId) is not { } formDict || PdfObjectMap.Name(formDict, "/Subtype") != "/Form")
            {
                return;
            }

            if (!activeForms.Add(targetId))
            {
                return;
            }

            try
            {
                var formMatrix = PdfMatrix.Identity;
                if (formDict.TryGetProperty("/Matrix", out var matrixElement) && matrixElement.ValueKind == JsonValueKind.Array && matrixElement.GetArrayLength() == 6)
                {
                    var m = matrixElement.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    formMatrix = new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]);
                }

                var formResources = resources;
                var formOwner = owner;
                if (formDict.TryGetProperty("/Resources", out var formResourceElement))
                {
                    if (PdfObjectMap.TryGetRef(formResourceElement, out var resourceId))
                    {
                        formResources = _structure.ValueOf(resourceId);
                        formOwner = resourceId;
                    }
                    else
                    {
                        formResources = formResourceElement;
                        formOwner = targetId;
                    }
                }

                visitedOwners.Add(formOwner);
                ScanContent(ReadExported(targetId), formMatrix.Then(ctm), formResources, formOwner, usage, visitedOwners, depth + 1, activeForms);
            }
            finally
            {
                activeForms.Remove(targetId);
            }
        });
    }

    /// <summary>(object holding a resource dictionary, object it points to) for every /XObject entry in the live objects of the file.</summary>
    private IEnumerable<(int Owner, int Target)> FindResourceReferences()
    {
        foreach (var id in _reachable)
        {
            if (_structure.ValueOf(id) is not { } value)
            {
                continue;
            }

            foreach (var target in XObjectTargets(value, 0))
            {
                yield return (id, target);
            }
        }
    }

    private static IEnumerable<int> XObjectTargets(JsonElement element, int depth)
    {
        if (depth > 12)
        {
            yield break;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "/XObject" && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entry in property.Value.EnumerateObject())
                        {
                            if (PdfObjectMap.TryGetRef(entry.Value, out var id))
                            {
                                yield return id;
                            }
                        }
                    }
                    else
                    {
                        foreach (var id in XObjectTargets(property.Value, depth + 1))
                        {
                            yield return id;
                        }
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var id in XObjectTargets(item, depth + 1))
                    {
                        yield return id;
                    }
                }

                break;
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Exporting streams
    // ------------------------------------------------------------------------------------------------------

    private async Task ExportAsync(int[] ids, string tag)
    {
        var jsonPath = Path.Combine(_workDirectory, $"export-{tag}-{ids[0]}.json");
        var prefix = Path.Combine(_workDirectory, $"stream-{tag}-");
        var selectors = await QpdfRunner.WriteArgumentFileAsync(
            Path.Combine(_workDirectory, $"objects-{tag}-{ids[0]}.txt"), ids.Select(id => $"--json-object={id}"), _token).ConfigureAwait(false);
        var args = new List<string>
        {
            _inputPdf, jsonPath, "--json-output", "--json-stream-data=file",
            $"--json-stream-prefix={prefix}", "--decode-level=generalized", selectors
        };

        var result = await _qpdf.RunAsync(args, _token).ConfigureAwait(false);
        if (!QpdfRunner.Succeeded(result) || !File.Exists(jsonPath))
        {
            throw new IOException($"qpdf could not export streams: {result.StandardError}");
        }

        // Only each stream dictionary is kept (as a small standalone copy); the batch's whole JSON tree is released at once.
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(jsonPath, _token).ConfigureAwait(false));
        foreach (var property in document.RootElement.GetProperty("qpdf")[1].EnumerateObject())
        {
            if (!PdfObjectMap.TryParseObjectKey(property.Name, out var id) || !property.Value.TryGetProperty("stream", out var stream))
            {
                continue;
            }

            if (stream.TryGetProperty("datafile", out var file) && file.GetString() is { } path)
            {
                _exportedFiles[id] = path;
            }

            if (stream.TryGetProperty("dict", out var dict))
            {
                _exportedDicts[id] = dict.Clone();
            }
        }

        try
        {
            File.Delete(jsonPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; the work directory is removed at the end
        }
    }

    private byte[] ReadExported(int id)
        => _exportedFiles.TryGetValue(id, out var path) && File.Exists(path)
            ? File.ReadAllBytes(path)
            : throw new FormatException($"Stream {id} was not exported.");

    /// <summary>Decoded pictures can be large; drop them as soon as a batch is done.</summary>
    private void ForgetExports(IEnumerable<int> ids)
    {
        foreach (var id in ids)
        {
            if (_exportedFiles.Remove(id, out var path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best effort; the work directory is removed at the end
                }
            }

            _exportedDicts.Remove(id);
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // One picture
    // ------------------------------------------------------------------------------------------------------

    private enum EncodingKind
    {
        Jpeg,
        Png
    }

    /// <summary>What a picture becomes: shareable between identical pictures because it holds no per-object state.</summary>
    private sealed record ImageEncoding(
        EncodingKind Kind,
        FilePath File,
        long Size,
        int Width,
        int Height,
        string Description,
        long ReferenceSize,
        PngPdfStream? Png = null,
        string? ColorSpaceJson = null);

    private readonly record struct ImageUpdate(JsonNode Node, long Saved, bool UsesPredictor, int OldWidth, int OldHeight, int NewWidth, int NewHeight);

    private async Task OptimiseImagesAsync(List<int> images, Dictionary<int, ImageUsage> usage, ConcurrentDictionary<int, ImageUpdate> results)
    {
        var done = 0;
        var batchNumber = 0;
        foreach (var batch in images.Chunk(ExportBatchSize))
        {
            _token.ThrowIfCancellationRequested();
            await ExportAsync(batch, $"images{batchNumber++}").ConfigureAwait(false);
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = _token }, async (id, _) =>
            {
                var number = Interlocked.Increment(ref done);
                _context.ReportProgress(30 + 50.0 * number / images.Count, $"Optimising picture {number} of {images.Count}");
                if (await TryOptimiseImageAsync(id, usage.GetValueOrDefault(id)).ConfigureAwait(false) is { } update)
                {
                    results[id] = update;
                }
            }).ConfigureAwait(false);

            // Soft masks are needed again by the mask pass, which exports them on its own.
            ForgetExports(batch);

            // The batch's decoded pictures are garbage now; don't let them wait for an idle moment that a long run never has.
            MemoryReclaimer.ReclaimIfPressured();
        }
    }

    private async Task<ImageUpdate?> TryOptimiseImageAsync(int id, ImageUsage? usage)
    {
        if (!_exportedDicts.TryGetValue(id, out var dict) || !_exportedFiles.TryGetValue(id, out var file) || !File.Exists(file))
        {
            return null;
        }

        if (_maskImages.Contains(id))
        {
            return null;
        }

        var length = new FileInfo(file).Length;
        if (length < _options.MinimumImageBytes || length > MaxImageBytes)
        {
            return null;
        }

        var width = PdfObjectMap.Int(dict, "/Width");
        var height = PdfObjectMap.Int(dict, "/Height");
        if (width is not > 0 || height is not > 0)
        {
            return null;
        }

        var components = ColourComponents(dict);
        if (components is null || IsSkippable(dict, components.Value))
        {
            return null;
        }

        var filter = PdfObjectMap.SingleFilter(dict);
        var isJpeg = filter == "/DCTDecode";
        var isRaw = !PdfObjectMap.HasFilter(dict) && PdfObjectMap.Int(dict, "/BitsPerComponent") == 8 && length == (long)width.Value * height.Value * components.Value;
        if (!isJpeg && !isRaw)
        {
            return null;
        }

        try
        {
            var scale = ScaleFor(usage, width.Value, height.Value);
            var key = await ShareKeyAsync(dict, file, scale).ConfigureAwait(false);
            var reference = ReferenceSize(id, length);
            var pixels = (long)width.Value * height.Value;
            var shared = _shared.GetOrAdd(key, _ => new Lazy<Task<ImageEncoding?>>(async () =>
            {
                using var lease = await _budget.AcquireAsync(pixels, _token).ConfigureAwait(false);
                return isJpeg
                    ? await OptimiseJpegAsync(id, dict, file, length, width.Value, height.Value, components.Value, scale, usage).ConfigureAwait(false)
                    : await OptimiseRawBitmapAsync(dict, file, reference, width.Value, height.Value, components.Value, scale).ConfigureAwait(false);
            }));
            var encoding = await shared.Value.ConfigureAwait(false);
            return encoding is null ? null : BuildUpdate(dict, encoding, width.Value, height.Value);
        }
        catch (Exception ex) when (ex is MagickException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            Log.Warning($"PDF picture {id} left unchanged: {ex.Message}");
            return null;
        }
    }

    /// <summary>Same bytes, same way of reading them, same placement size: the same result.</summary>
    private async Task<string> ShareKeyAsync(JsonElement dict, string file, double? scale)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, _token).ConfigureAwait(false));
        static string Raw(JsonElement d, string name) => d.TryGetProperty(name, out var v) ? v.GetRawText() : string.Empty;
        return string.Join('|', hash, Raw(dict, "/Width"), Raw(dict, "/Height"), Raw(dict, "/ColorSpace"), Raw(dict, "/Filter"),
            Raw(dict, "/DecodeParms"), Raw(dict, "/BitsPerComponent"), scale?.ToString("R", CultureInfo.InvariantCulture) ?? "-");
    }

    private long ReferenceSize(int id, long decodedLength)
    {
        // The Flate original is compressed on disk; that is what a replacement has to beat, not the decoded size.
        var stored = _structure.ValueOf(id) is { } original ? PdfObjectMap.Int(original, "/Length") ?? 0 : 0;
        return stored > 0 ? stored : decodedLength / 2;
    }

    /// <summary>Image masks, colour-key masks, inverted decode arrays and 1-bit data are not touched.</summary>
    private static bool IsSkippable(JsonElement dict, int components)
    {
        if (dict.TryGetProperty("/ImageMask", out var mask) && mask.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (dict.TryGetProperty("/Decode", out var decode) && !IsIdentityDecode(decode, components))
        {
            return true;
        }

        // A /Mask array is a colour key: it only works with the exact original samples.
        return dict.TryGetProperty("/Mask", out var m) && m.ValueKind == JsonValueKind.Array;
    }

    /// <summary>Many producers spell out the default: [0 1] for one component, [0 1 0 1 0 1] for three.</summary>
    internal static bool IsIdentityDecode(JsonElement decode, int components)
    {
        if (decode.ValueKind != JsonValueKind.Array || decode.GetArrayLength() != components * 2)
        {
            return false;
        }

        var index = 0;
        foreach (var item in decode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || item.GetDouble() != (index % 2 == 0 ? 0d : 1d))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private int? ColourComponents(JsonElement dict)
    {
        if (!dict.TryGetProperty("/ColorSpace", out var space))
        {
            return null;
        }

        return ComponentsOf(_structure.Deref(space) ?? space, 0);
    }

    private int? ComponentsOf(JsonElement resolved, int depth)
    {
        if (depth > 2)
        {
            return null;
        }

        if (resolved.ValueKind == JsonValueKind.String)
        {
            return resolved.GetString() switch
            {
                "/DeviceGray" or "/CalGray" => 1,
                "/DeviceRGB" or "/CalRGB" => 3,
                _ => null
            };
        }

        if (resolved.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // [/DeviceRGB] means the same as /DeviceRGB.
        if (resolved.GetArrayLength() == 1)
        {
            return ComponentsOf(resolved[0], depth + 1);
        }

        if (resolved.GetArrayLength() != 2 || resolved[0].ValueKind != JsonValueKind.String)
        {
            return null;
        }

        // macOS Quartz writes [/CalRGB << ... >>] and [/CalGray << ... >>]; the samples are read the same way, only the
        // interpretation of the values differs, and that part of the dictionary is kept.
        switch (resolved[0].GetString())
        {
            case "/CalGray":
                return 1;
            case "/CalRGB":
                return 3;
            case "/ICCBased" when PdfObjectMap.TryGetRef(resolved[1], out var profileId) && _structure.ValueOf(profileId) is { } profile
                                  && PdfObjectMap.Int(profile, "/N") is 1 or 3:
                return PdfObjectMap.Int(profile, "/N");
            default:
                return null;
        }
    }

    private double? ScaleFor(ImageUsage? usage, int width, int height)
    {
        if (usage is null || usage.Unknown || usage.Placements.Count == 0)
        {
            return null;
        }

        // Every place the picture is drawn must keep at least the target resolution after the resample.
        var scale = 0.0;
        foreach (var (widthPts, heightPts) in usage.Placements)
        {
            if (widthPts < 1 || heightPts < 1)
            {
                return null;
            }

            var dpiX = width * 72.0 / widthPts;
            var dpiY = height * 72.0 / heightPts;
            scale = Math.Max(scale, Math.Max(TargetDpi / dpiX, TargetDpi / dpiY));
        }

        return scale <= _options.MinimumDownscaleFactor ? scale : null;
    }

    /// <summary>
    /// /DecodeParms on a JPEG normally only carries /ColorTransform. The value 1 on a three-component picture is the default
    /// (YCbCr), which is how every re-encode writes it, so spelling it out must not disqualify the picture.
    /// </summary>
    internal static bool JpegDecodeParmsAreDefault(JsonElement dict, JpegInfo jpeg)
    {
        if (!dict.TryGetProperty("/DecodeParms", out var parms))
        {
            return true;
        }

        bool Check(JsonElement item)
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Null:
                    return true;
                case JsonValueKind.Object:
                    foreach (var property in item.EnumerateObject())
                    {
                        if (property.Name != "/ColorTransform" || property.Value.ValueKind != JsonValueKind.Number)
                        {
                            return false;
                        }

                        var transform = property.Value.GetInt32();
                        var standard = jpeg.Components == 3 ? transform == 1 && jpeg.AdobeTransform != 0 : jpeg.Components == 1;
                        if (!standard)
                        {
                            return false;
                        }
                    }

                    return true;
                case JsonValueKind.Array:
                    return item.GetArrayLength() <= 1 && item.EnumerateArray().All(Check);
                default:
                    return false;
            }
        }

        return Check(parms);
    }

    private async Task<ImageEncoding?> OptimiseJpegAsync(int id, JsonElement dict, string file, long length, int width, int height, int components, double? scale, ImageUsage? usage)
    {
        // The file is only inspected here; reading it in a helper keeps its bytes from living in this method's state for the
        // whole (long) encode below.
        var (info, orientation) = await InspectJpegAsync(file).ConfigureAwait(false);
        if (info is not { } jpeg || jpeg.Components != components || jpeg.Components is not (1 or 3))
        {
            return null;
        }

        // Anything beyond an explicit default changes how the samples are read; a re-encode would have to reproduce it.
        if (!JpegDecodeParmsAreDefault(dict, jpeg))
        {
            return null;
        }

        var staged = _temp.New("squish-pdfimg", "jpg");
        File.Copy(file, staged.Value, overwrite: true);

        var best = new ImageEncoding(EncodingKind.Jpeg, staged, length, jpeg.Width, jpeg.Height, "unchanged", length);

        // 1. Lossless Huffman / progressive recompression: identical pixels.
        if (await _encoders.RecompressJpegLosslessAsync(staged, _token).ConfigureAwait(false) is { } lossless)
        {
            var size = new FileInfo(lossless.Value).Length;
            var (reread, _) = await ReadJpegHeaderAsync(lossless.Value, _token).ConfigureAwait(false);
            if (size < best.Size && reread is { } r && r.Components == components && r.Width == jpeg.Width && r.Height == jpeg.Height)
            {
                best = best with { File = lossless, Size = size, Description = "lossless" };
            }
        }

        // 2. Perceptual re-encode (and downsample), unless a colour-sensitive detail makes pixel access unsafe.
        var rawRgbCoded = jpeg.Components == 3 && jpeg.AdobeTransform == 0;
        if (orientation == 1 && !rawRgbCoded)
        {
            // The pipeline replaces its input in place, so it gets its own copy and the lossless candidate stays intact.
            var lossyInput = _temp.New("squish-pdfimg-lossy", "jpg");
            File.Copy(staged.Value, lossyInput.Value, overwrite: true);
            var lossy = await RunPipelineAsync(lossyInput, convertPng: false, scale, jpeg.Width, jpeg.Height, components, minPixels: 0).ConfigureAwait(false);
            if (lossy is { Kind: EncodingKind.Jpeg } candidate && candidate.Size < best.Size)
            {
                best = candidate with { ReferenceSize = length, Description = scale is null ? "re-encoded" : "downsampled" };
            }
        }

        Log.Debug($"PDF picture {id}: {jpeg.Width}x{jpeg.Height}, best {best.Description} {best.Size} of {length} bytes, usage {(usage is null ? "none" : usage.Unknown ? "unknown" : usage.Placements.Count + " placements")}");
        return best.Description == "unchanged" || best.Size >= length * 0.97 ? null : best;
    }

    private async Task<(JpegInfo? Info, int Orientation)> InspectJpegAsync(string file)
    {
        var (info, header) = await ReadJpegHeaderAsync(file, _token).ConfigureAwait(false);
        if (info is null)
        {
            return (null, 1);
        }

        var orientation = JpegSegments.Extract(header).Exif is { Length: > 0 } exif ? JpegSegments.ReadOrientation(exif) : 1;
        return (info, orientation);
    }

    // Everything JpegInfo / the EXIF reader need sits in the marker segments before the first scan, so only that prefix is read:
    // a multi-megabyte picture no longer becomes a multi-megabyte array on the Large Object Heap just to learn its size.
    private const int JpegHeaderStartBytes = 64 * 1024;
    private const int JpegHeaderMaxBytes = 4 * 1024 * 1024;

    internal static async Task<(JpegInfo? Info, byte[] Header)> ReadJpegHeaderAsync(string file, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var size = (long)JpegHeaderStartBytes;
        while (true)
        {
            var header = new byte[(int)Math.Min(size, length)];
            stream.Position = 0;
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var info = JpegInfo.TryRead(header);

            // A null result may only mean the segments are longer than what was read (big ICC/XMP blocks), so widen the window.
            if (info is not null || header.Length >= length || size >= JpegHeaderMaxBytes)
            {
                return (info, header);
            }

            size *= 4;
        }
    }

    private async Task WriteRawFileAsPngAsync(string file, int width, int height, int components, FilePath destination)
    {
        // The raw bitmap (tens of MB) is dropped as soon as the PNG exists, before the optimising pipeline starts.
        var raw = await File.ReadAllBytesAsync(file, _token).ConfigureAwait(false);
        await Task.Run(() => WriteRawAsPng(raw, width, height, components, destination), _token).ConfigureAwait(false);
    }

    private async Task<ImageEncoding?> OptimiseRawBitmapAsync(JsonElement dict, string file, long reference, int width, int height, int components, double? scale)
    {
        if ((long)width * height < 20_000)
        {
            return null;
        }

        var staged = _temp.New("squish-pdfbmp", "png");
        await WriteRawFileAsPngAsync(file, width, height, components, staged).ConfigureAwait(false);

        var converted = await RunPipelineAsync(staged, convertPng: true, scale, width, height, components, minPixels: 20_000).ConfigureAwait(false);
        if (converted is not { } candidate)
        {
            return null;
        }

        if (candidate.Kind == EncodingKind.Jpeg)
        {
            // The Flate original is already deflated; the JPEG has to clearly beat it to be worth the loss.
            return candidate.Size >= reference * 0.9
                ? null
                : candidate with { ReferenceSize = reference, Description = "photographic bitmap converted to JPEG" };
        }

        // Screenshots, charts and logos stay lossless (or palette-quantised under the perceptual guard) as PNG scanlines.
        if (candidate.Png is not { } png || MapPngColour(dict, png, components) is not { } colour)
        {
            return null;
        }

        return candidate.Size >= reference * 0.95
            ? null
            : candidate with { ReferenceSize = reference, ColorSpaceJson = colour, Description = "bitmap re-encoded as PNG scanlines" };
    }

    private static void WriteRawAsPng(byte[] raw, int width, int height, int components, FilePath destination)
    {
        ImageIO.EnsureConfigured();
        var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, components == 3 ? "RGB" : "I");
        using var image = new MagickImage();
        image.ReadPixels(raw, settings);
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
        image.Format = MagickFormat.Png;
        image.Write(destination.Value);
    }

    private static string NormaliseSpaceJson(JsonElement space)
        => space.ValueKind == JsonValueKind.Array && space.GetArrayLength() == 1 && space[0].ValueKind == JsonValueKind.String
            ? space[0].GetRawText()
            : space.GetRawText();

    /// <summary>
    /// The /ColorSpace entry a PNG result needs, as JSON, or null when the PNG cannot stand in for the original picture. The
    /// pixel values are not colour-managed here, so the original space (an ICC profile, say) stays the one that interprets them;
    /// a palette becomes an /Indexed space on top of it.
    /// </summary>
    internal static string? MapPngColour(JsonElement dict, PngPdfStream png, int components)
    {
        if (!dict.TryGetProperty("/ColorSpace", out var space))
        {
            return null;
        }

        var original = NormaliseSpaceJson(space);
        switch (png.ColorType)
        {
            case 2:
                return components == 3 ? original : null;

            case 0:
                if (components == 1)
                {
                    return original;
                }

                // Lossless colour-to-grey reduction (R = G = B) is only a pure re-labelling for a device colour space.
                return components == 3 && original == "\"/DeviceRGB\"" ? "\"/DeviceGray\"" : null;

            case 3 when png.Palette is { Length: > 0 } palette:
                var entries = palette.Length / 3;
                byte[] lookup = palette;
                if (components == 1)
                {
                    lookup = new byte[entries];
                    for (var i = 0; i < entries; i++)
                    {
                        if (palette[i * 3] != palette[i * 3 + 1] || palette[i * 3] != palette[i * 3 + 2])
                        {
                            return null;
                        }

                        lookup[i] = palette[i * 3];
                    }
                }

                return $"[\"/Indexed\",{original},{entries - 1},\"b:{Convert.ToHexString(lookup)}\"]";

            default:
                return null;
        }
    }

    /// <summary>
    /// Runs the shared image pipeline on a staged picture and returns what it ships: a JPEG, or (when
    /// <paramref name="convertPng"/> is set) a PNG whose scanlines can be carried into the PDF. Null when nothing acceptable came out.
    /// </summary>
    private async Task<ImageEncoding?> RunPipelineAsync(FilePath staged, bool convertPng, double? scale, int width, int height, int components, long minPixels)
    {
        var baseline = _options.ImageOptions;
        var longEdge = scale is { } s ? (int)Math.Ceiling(Math.Max(width, height) * s) : 0;
        var imageOptions = baseline with
        {
            FormatsToConvertToJpeg = convertPng ? new HashSet<string>(["png"], StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FormatsToConvertToPng = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            JpegConversionMinimumPixels = minPixels > 0 ? minPixels : baseline.JpegConversionMinimumPixels,
            DownscaleRetina = longEdge > 0,
            RetinaLongEdgePixels = longEdge > 0 ? longEdge : baseline.RetinaLongEdgePixels,
            PreserveDates = false,
            RequireSizeImprovement = true,
            AdvancedCodecs = baseline.AdvancedCodecs with { AllowWebpOutput = false, AllowAvifOutput = false }
        };

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [OptimisationMetadata.OutputReplaceOriginal] = true,
            [OptimisationMetadata.ImageAggressive] = _aggressive,
            [OptimisationMetadata.PixelBudgetHeld] = true
        };
        var request = new OptimisationRequest(ItemType.Image, staged, metadata: metadata);
        var context = new OptimiserExecutionContext(request.RequestId, static _ => { });
        var result = await new ImageOptimiser(imageOptions).OptimiseAsync(request, context, _token).ConfigureAwait(false);
        if (result.Status != OptimisationStatus.Succeeded || result.OutputPath is not { } output || !File.Exists(output.Value))
        {
            return null;
        }

        if (!string.Equals(output.Value, staged.Value, StringComparison.OrdinalIgnoreCase))
        {
            _temp.Track(output);
        }

        var extension = (output.Extension ?? string.Empty).ToLowerInvariant();
        if (extension == "png" && convertPng)
        {
            return await ReadPngCandidateAsync(output).ConfigureAwait(false);
        }

        if (extension is not ("jpg" or "jpeg"))
        {
            return null;
        }

        var current = output;
        var (info, _) = await ReadJpegHeaderAsync(current.Value, _token).ConfigureAwait(false);
        if (info is not { } jpeg)
        {
            return null;
        }

        if (components == 1 && jpeg.Components == 3)
        {
            // The pipeline encodes RGB; a grey picture must stay single-component, which jpegtran does losslessly.
            var grey = await ToGreyscaleAsync(current).ConfigureAwait(false);
            if (grey is null)
            {
                return null;
            }

            current = grey.Value;
            (info, _) = await ReadJpegHeaderAsync(current.Value, _token).ConfigureAwait(false);
            if (info is not { } greyInfo)
            {
                return null;
            }

            jpeg = greyInfo;
        }

        var encodedLength = new FileInfo(current.Value).Length;
        return jpeg.Components == components
            ? new ImageEncoding(EncodingKind.Jpeg, current, encodedLength, jpeg.Width, jpeg.Height, "re-encoded", encodedLength)
            : null;
    }

    /// <summary>
    /// Pulls the zlib scanline data (and palette) out of an optimised PNG. The pixel data is not touched, so what oxipng or
    /// pngquant decided is exactly what the PDF gets - no second deflate pass.
    /// </summary>
    private async Task<ImageEncoding?> ReadPngCandidateAsync(FilePath png)
    {
        var bytes = await File.ReadAllBytesAsync(png.Value, _token).ConfigureAwait(false);
        if (PngPdfStream.TryRead(bytes) is not { } stream)
        {
            return null;
        }

        var data = _temp.New("squish-pdfidat", "bin");
        await File.WriteAllBytesAsync(data.Value, stream.Idat, _token).ConfigureAwait(false);
        var size = stream.Idat.LongLength + (stream.Palette?.LongLength ?? 0);
        // The scanline data now lives in `data`; this record is cached for the whole run and only needs the header fields.
        return new ImageEncoding(EncodingKind.Png, data, size, stream.Width, stream.Height, "PNG", size, stream with { Idat = [] });
    }

    private async Task<FilePath?> ToGreyscaleAsync(FilePath source)
    {
        if (_encoders.Tools.Jpegtran is not { } jpegtran)
        {
            return null;
        }

        var output = _temp.New("squish-pdfgrey", "jpg");
        var result = await ProcessRunner.RunAsync(jpegtran, ["-grayscale", "-optimize", "-copy", "none", "-outfile", output.Value, source.Value],
            ProcessRunnerOptions.Create(throwOnError: false), _token).ConfigureAwait(false);
        return result.ExitCode == 0 && File.Exists(output.Value) ? output : null;
    }

    private ImageUpdate? BuildUpdate(JsonElement dict, ImageEncoding best, int oldWidth, int oldHeight)
    {
        var node = JsonNode.Parse(dict.GetRawText())!.AsObject();
        node.Remove("/Length");
        node.Remove("/Decode");
        if (_options.StripMetadata)
        {
            node.Remove("/Metadata");
            node.Remove("/PieceInfo");
        }

        node["/Width"] = best.Width;
        node["/Height"] = best.Height;

        if (best.Kind == EncodingKind.Jpeg)
        {
            node["/Filter"] = "/DCTDecode";
            node.Remove("/DecodeParms");
            node["/BitsPerComponent"] = 8;
        }
        else
        {
            node["/Filter"] = "/FlateDecode";
            var png = best.Png!;
            node["/DecodeParms"] = new JsonObject
            {
                ["/Predictor"] = 15,
                ["/Colors"] = png.Colors,
                ["/BitsPerComponent"] = png.BitDepth,
                ["/Columns"] = best.Width
            };
            node["/BitsPerComponent"] = png.BitDepth;
            node["/ColorSpace"] = JsonNode.Parse(best.ColorSpaceJson!);
        }

        // The temp scope disposes with the pass, which is after the update has been applied.
        var stream = new JsonObject { ["dict"] = node, ["datafile"] = best.File.Value };
        return new ImageUpdate(new JsonObject { ["stream"] = stream }, best.ReferenceSize - best.Size, best.Kind == EncodingKind.Png, oldWidth, oldHeight, best.Width, best.Height);
    }

    // ------------------------------------------------------------------------------------------------------
    // Soft masks
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 8-bit soft masks stay lossless (JPEG ringing would show as halos on the alpha edge) but are shrunk to match their picture
    /// and stored with PNG row filters. A mask kept at 3000x2000 beside a picture reduced to 440x293 would otherwise outweigh it.
    /// </summary>
    private async Task OptimiseMasksAsync(ConcurrentDictionary<int, ImageUpdate> results)
    {
        // A mask shared by several pictures would have to suit all of them; leave those alone.
        var masks = _softMaskParents.Where(m => m.Value.Count == 1 && _reachable.Contains(m.Key) && !results.ContainsKey(m.Key))
            .Select(m => m.Key).OrderBy(id => id).ToList();
        var batchNumber = 0;
        foreach (var batch in masks.Chunk(ExportBatchSize))
        {
            _token.ThrowIfCancellationRequested();
            await ExportAsync(batch, $"masks{batchNumber++}").ConfigureAwait(false);
            await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = _token }, async (id, _) =>
            {
                try
                {
                    ImageUpdate? parent = results.TryGetValue(_softMaskParents[id][0], out var parentUpdate) ? parentUpdate : null;
                    if (await TryOptimiseMaskAsync(id, parent).ConfigureAwait(false) is { } update)
                    {
                        results[id] = update;
                    }
                }
                catch (Exception ex) when (ex is MagickException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
                {
                    Log.Warning($"PDF mask {id} left unchanged: {ex.Message}");
                }
            }).ConfigureAwait(false);

            ForgetExports(batch);
        }
    }

    private async Task<ImageUpdate?> TryOptimiseMaskAsync(int id, ImageUpdate? parent)
    {
        if (!_exportedDicts.TryGetValue(id, out var dict) || !_exportedFiles.TryGetValue(id, out var file) || !File.Exists(file))
        {
            return null;
        }

        var width = PdfObjectMap.Int(dict, "/Width");
        var height = PdfObjectMap.Int(dict, "/Height");
        var length = new FileInfo(file).Length;
        if (width is not > 0 || height is not > 0 || length < _options.MinimumImageBytes || length > MaxImageBytes)
        {
            return null;
        }

        // /Matte is premultiplied against the picture and requires the same size; a stencil is 1-bit and not handled here.
        if (PdfObjectMap.HasFilter(dict) || dict.TryGetProperty("/Matte", out _) || PdfObjectMap.Int(dict, "/BitsPerComponent") != 8
            || length != (long)width.Value * height.Value || !IsPlainGrey(dict))
        {
            return null;
        }

        var newWidth = width.Value;
        var newHeight = height.Value;
        if (parent is { } p && p.NewWidth < p.OldWidth && p.NewHeight < p.OldHeight)
        {
            newWidth = Math.Max(1, (int)Math.Round(width.Value * (double)p.NewWidth / p.OldWidth));
            newHeight = Math.Max(1, (int)Math.Round(height.Value * (double)p.NewHeight / p.OldHeight));
        }

        using var lease = await _budget.AcquireAsync((long)width.Value * height.Value, _token).ConfigureAwait(false);
        var raw = await File.ReadAllBytesAsync(file, _token).ConfigureAwait(false);
        var staged = _temp.New("squish-pdfmask", "png");
        await Task.Run(() =>
        {
            ImageIO.EnsureConfigured();
            using var image = new MagickImage();
            image.ReadPixels(raw, new PixelReadSettings((uint)width.Value, (uint)height.Value, StorageType.Char, "I"));
            if (newWidth != width.Value || newHeight != height.Value)
            {
                image.FilterType = FilterType.Lanczos;
                image.Resize((uint)newWidth, (uint)newHeight);
            }

            image.ColorType = ColorType.Grayscale;
            image.Depth = 8;
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            image.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
            image.Format = MagickFormat.Png;
            image.Write(staged.Value);
        }, _token).ConfigureAwait(false);

        var best = staged;
        if (await _encoders.OptimisePngLosslessAsync(staged, 4, zopfli: false, "all", _token).ConfigureAwait(false) is { } optimised
            && new FileInfo(optimised.Value).Length < new FileInfo(best.Value).Length)
        {
            best = optimised;
        }

        // Only greyscale scanlines can stand in for a /DeviceGray mask; a palette or RGB result is not what a mask expects.
        if (PngPdfStream.TryRead(await File.ReadAllBytesAsync(best.Value, _token).ConfigureAwait(false)) is not { ColorType: 0 } png)
        {
            return null;
        }

        var reference = ReferenceSize(id, length);
        if (png.Idat.LongLength >= reference * 0.9)
        {
            return null;
        }

        var data = _temp.New("squish-pdfmaskdata", "bin");
        await File.WriteAllBytesAsync(data.Value, png.Idat, _token).ConfigureAwait(false);

        var node = JsonNode.Parse(dict.GetRawText())!.AsObject();
        node.Remove("/Length");
        node.Remove("/Decode");
        node["/Width"] = png.Width;
        node["/Height"] = png.Height;
        node["/BitsPerComponent"] = png.BitDepth;
        node["/Filter"] = "/FlateDecode";
        node["/DecodeParms"] = new JsonObject
        {
            ["/Predictor"] = 15,
            ["/Colors"] = 1,
            ["/BitsPerComponent"] = png.BitDepth,
            ["/Columns"] = png.Width
        };

        var stream = new JsonObject { ["dict"] = node, ["datafile"] = data.Value };
        return new ImageUpdate(new JsonObject { ["stream"] = stream }, reference - png.Idat.LongLength, true, width.Value, height.Value, png.Width, png.Height);
    }

    private static bool IsPlainGrey(JsonElement dict)
    {
        if (PdfObjectMap.Name(dict, "/ColorSpace") != "/DeviceGray")
        {
            return false;
        }

        return !dict.TryGetProperty("/Decode", out var decode) || IsIdentityDecode(decode, 1);
    }

    // ------------------------------------------------------------------------------------------------------
    // Metadata that lives in object dictionaries
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Page thumbnails (/Thumb) and application-private data (/PieceInfo) on pages and the catalog; /PieceInfo and per-object
    /// XMP (/Metadata) on form and picture XObjects, where Illustrator, InDesign and Photoshop leave their private copies.
    /// </summary>
    private IEnumerable<(string Key, JsonNode Node)> BuildMetadataUpdates()
    {
        foreach (var id in _reachable)
        {
            if (!_structure.IsStream(id))
            {
                if (_structure.ValueOf(id) is not { ValueKind: JsonValueKind.Object } value)
                {
                    continue;
                }

                var type = PdfObjectMap.Name(value, "/Type");
                if (type is not ("/Page" or "/Catalog") || (!value.TryGetProperty("/Thumb", out _) && !value.TryGetProperty("/PieceInfo", out _)))
                {
                    continue;
                }

                var node = JsonNode.Parse(value.GetRawText())!.AsObject();
                node.Remove("/Thumb");
                node.Remove("/PieceInfo");
                yield return ($"obj:{id} 0 R", new JsonObject { ["value"] = node });
                continue;
            }

            if (_structure.ValueOf(id) is not { ValueKind: JsonValueKind.Object } dict
                || PdfObjectMap.Name(dict, "/Subtype") is not ("/Form" or "/Image")
                || (!dict.TryGetProperty("/PieceInfo", out _) && !dict.TryGetProperty("/Metadata", out _)))
            {
                continue;
            }

            // A dictionary-only stream update keeps the stream data as it is.
            var streamDict = JsonNode.Parse(dict.GetRawText())!.AsObject();
            streamDict.Remove("/PieceInfo");
            streamDict.Remove("/Metadata");
            streamDict.Remove("/Length");
            yield return ($"obj:{id} 0 R", new JsonObject { ["stream"] = new JsonObject { ["dict"] = streamDict } });
        }
    }
}
