using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

internal sealed record PdfImagePassResult(int ImagesChanged, long BytesSaved, bool Changed);

/// <summary>
/// Rewrites the embedded pictures of a PDF through qpdf's JSON interface, leaving every other object alone:
/// <list type="bullet">
/// <item>DCT (JPEG) pictures: lossless Huffman/progressive recompression, then a perceptually guarded re-encode, downsampled when
/// the picture is drawn at a much higher resolution than the target.</item>
/// <item>8-bit RGB/Gray Flate pictures: converted to JPEG when the content is photographic and the guard accepts it.</item>
/// <item>Everything else (1-bit scans, CCITT, JBIG2, JPX, CMYK, indexed, masks with colour keys, inverted /Decode) is untouched.</item>
/// </list>
/// </summary>
internal sealed class PdfImagePass
{
    private const int ExportBatchSize = 40;
    private const int MaxTrackedStreams = 6000;
    private const int MaxFormDepth = 6;
    private const long MaxImageBytes = 256L * 1024 * 1024;

    private readonly QpdfRunner _qpdf;
    private readonly PdfOptimiserOptions _options;
    private readonly bool _aggressive;
    private readonly string _workDirectory;
    private readonly OptimiserExecutionContext _context;
    private readonly CancellationToken _token;
    private readonly TempFileScope _temp = new();
    private readonly ImageEncoders _encoders;

    private PdfObjectMap _structure = null!;
    private string _inputPdf = string.Empty;
    private readonly Dictionary<int, string> _exportedFiles = new();
    private readonly Dictionary<int, JsonElement> _exportedDicts = new();
    private readonly List<JsonDocument> _exportDocuments = new();

    public PdfImagePass(QpdfRunner qpdf, PdfOptimiserOptions options, bool aggressive, string workDirectory, OptimiserExecutionContext context, CancellationToken token)
    {
        _qpdf = qpdf;
        _options = options;
        _aggressive = aggressive;
        _workDirectory = workDirectory;
        _context = context;
        _token = token;
        _encoders = new ImageEncoders(ImageToolset.Resolve(options.ImageOptions.AdvancedCodecs), _temp);
    }

    private int TargetDpi => _aggressive ? _options.AggressiveTargetDpi : _options.TargetDpi;

    public async Task<PdfImagePassResult> RunAsync(string inputPdf, string outputPdf, bool linearize)
    {
        try
        {
            return await RunCoreAsync(inputPdf, outputPdf, linearize).ConfigureAwait(false);
        }
        finally
        {
            foreach (var document in _exportDocuments)
            {
                document.Dispose();
            }

            _structure?.Dispose();
            _temp.Dispose();
        }
    }

    private async Task<PdfImagePassResult> RunCoreAsync(string inputPdf, string outputPdf, bool linearize)
    {
        _inputPdf = inputPdf;
        var structurePath = Path.Combine(_workDirectory, "structure.json");
        var export = await _qpdf.RunAsync(
            [inputPdf, structurePath, "--json-output", "--json-stream-data=none"], _token).ConfigureAwait(false);
        if (!QpdfRunner.Succeeded(export))
        {
            return new PdfImagePassResult(0, 0, false);
        }

        _structure = PdfObjectMap.Load(structurePath);

        var images = _structure.Ids.Where(IsImage).ToList();
        var updates = new JsonObject();

        var changed = 0;
        long saved = 0;

        if (images.Count > 0 && _options.OptimiseImages)
        {
            var usage = await FindImageUsageAsync(images).ConfigureAwait(false);
            var done = 0;
            foreach (var batch in images.Chunk(ExportBatchSize))
            {
                _token.ThrowIfCancellationRequested();
                await ExportAsync(batch, $"images{done}").ConfigureAwait(false);
                foreach (var id in batch)
                {
                    _token.ThrowIfCancellationRequested();
                    _context.ReportProgress(30 + 55.0 * done / images.Count, $"Optimising picture {done + 1} of {images.Count}");
                    done++;
                    var result = await TryOptimiseImageAsync(id, usage.GetValueOrDefault(id)).ConfigureAwait(false);
                    if (result is { } update)
                    {
                        updates[$"obj:{id} 0 R"] = update.Node;
                        changed++;
                        saved += update.Saved;
                    }
                }
            }
        }

        if (_options.StripMetadata)
        {
            foreach (var (key, node) in BuildMetadataUpdates())
            {
                updates[key] = node;
            }
        }

        if (updates.Count == 0)
        {
            return new PdfImagePassResult(0, 0, false);
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

        var args = new List<string> { inputPdf, outputPdf, $"--update-from-json={updatePath}" };
        args.AddRange(QpdfRunner.WriteOptions(linearize));
        var update2 = await _qpdf.RunAsync(args, _token).ConfigureAwait(false);
        if (!QpdfRunner.Succeeded(update2) || !File.Exists(outputPdf))
        {
            Log.Warning($"qpdf could not apply the picture updates: {update2.StandardError}");
            return new PdfImagePassResult(0, 0, false);
        }

        return new PdfImagePassResult(changed, saved, true);
    }

    // ------------------------------------------------------------------------------------------------------
    // Where is each picture drawn, and how large?
    // ------------------------------------------------------------------------------------------------------

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
            var forms = _structure.Ids.Where(id => _structure.IsStream(id) && _structure.ValueOf(id) is { } d && PdfObjectMap.Name(d, "/Subtype") == "/Form").ToList();
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

            var done = 0;
            foreach (var batch in streamIds.Chunk(ExportBatchSize * 5))
            {
                _token.ThrowIfCancellationRequested();
                await ExportAsync(batch, $"content{done}").ConfigureAwait(false);
                done += batch.Length;
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
                ScanContent(stream.ToArray(), PdfMatrix.Identity, page.Resources, page.ResourceOwner, usage, visitedOwners, 0, new HashSet<int>());
            }

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

    private sealed record PageInfo(List<int> ContentIds, JsonElement? Resources, int ResourceOwner);

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

            pages.Add(new PageInfo(contents, resources, owner));
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

    /// <summary>(object holding a resource dictionary, object it points to) for every /XObject entry in the file.</summary>
    private IEnumerable<(int Owner, int Target)> FindResourceReferences()
    {
        foreach (var id in _structure.Ids)
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
        var args = new List<string>
        {
            _inputPdf, jsonPath, "--json-output", "--json-stream-data=file",
            $"--json-stream-prefix={prefix}", "--decode-level=generalized"
        };
        args.AddRange(ids.Select(id => $"--json-object={id}"));

        var result = await _qpdf.RunAsync(args, _token).ConfigureAwait(false);
        if (!QpdfRunner.Succeeded(result) || !File.Exists(jsonPath))
        {
            throw new IOException($"qpdf could not export streams: {result.StandardError}");
        }

        var document = JsonDocument.Parse(await File.ReadAllBytesAsync(jsonPath, _token).ConfigureAwait(false));
        _exportDocuments.Add(document);
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
                _exportedDicts[id] = dict;
            }
        }
    }

    private byte[] ReadExported(int id)
        => _exportedFiles.TryGetValue(id, out var path) && File.Exists(path)
            ? File.ReadAllBytes(path)
            : throw new FormatException($"Stream {id} was not exported.");

    // ------------------------------------------------------------------------------------------------------
    // One picture
    // ------------------------------------------------------------------------------------------------------

    private readonly record struct ImageUpdate(JsonNode Node, long Saved);

    private async Task<ImageUpdate?> TryOptimiseImageAsync(int id, ImageUsage? usage)
    {
        if (!_exportedDicts.TryGetValue(id, out var dict) || !_exportedFiles.TryGetValue(id, out var file) || !File.Exists(file))
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
        if (width is not > 0 || height is not > 0 || IsSkippable(dict))
        {
            return null;
        }

        var components = ColourComponents(dict);
        if (components is null)
        {
            return null;
        }

        var filter = PdfObjectMap.Name(dict, "/Filter");
        try
        {
            if (filter == "/DCTDecode")
            {
                return await OptimiseJpegAsync(id, dict, file, length, width.Value, height.Value, components.Value, usage).ConfigureAwait(false);
            }

            if (filter is null && PdfObjectMap.Int(dict, "/BitsPerComponent") == 8 && length == (long)width.Value * height.Value * components.Value)
            {
                return await OptimiseRawBitmapAsync(id, dict, file, length, width.Value, height.Value, components.Value, usage).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is MagickException or IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            Log.Warning($"PDF picture {id} left unchanged: {ex.Message}");
        }

        return null;
    }

    /// <summary>Image masks, colour-key masks, inverted decode arrays and 1-bit data are not touched.</summary>
    private static bool IsSkippable(JsonElement dict)
    {
        if (dict.TryGetProperty("/ImageMask", out var mask) && mask.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (dict.TryGetProperty("/Decode", out _))
        {
            return true;
        }

        // A /Mask array is a colour key: it only works with the exact original samples.
        return dict.TryGetProperty("/Mask", out var m) && m.ValueKind == JsonValueKind.Array;
    }

    private int? ColourComponents(JsonElement dict)
    {
        if (!dict.TryGetProperty("/ColorSpace", out var space))
        {
            return null;
        }

        var resolved = _structure.Deref(space) ?? space;
        if (resolved.ValueKind == JsonValueKind.String)
        {
            return resolved.GetString() switch
            {
                "/DeviceGray" => 1,
                "/DeviceRGB" => 3,
                _ => null
            };
        }

        if (resolved.ValueKind == JsonValueKind.Array && resolved.GetArrayLength() == 2 && resolved[0].GetString() == "/ICCBased"
            && PdfObjectMap.TryGetRef(resolved[1], out var profileId) && _structure.ValueOf(profileId) is { } profile
            && PdfObjectMap.Int(profile, "/N") is 1 or 3)
        {
            return PdfObjectMap.Int(profile, "/N");
        }

        return null;
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

    private async Task<ImageUpdate?> OptimiseJpegAsync(int id, JsonElement dict, string file, long length, int width, int height, int components, ImageUsage? usage)
    {
        if (dict.TryGetProperty("/DecodeParms", out _))
        {
            // ColorTransform and friends change how the samples are read; a re-encode would have to reproduce them.
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(file, _token).ConfigureAwait(false);
        var info = JpegInfo.TryRead(bytes);
        if (info is not { } jpeg || jpeg.Components != components || jpeg.Components is not (1 or 3))
        {
            return null;
        }

        var staged = _temp.New("squish-pdfimg", "jpg");
        await File.WriteAllBytesAsync(staged.Value, bytes, _token).ConfigureAwait(false);

        ImageCandidateFile best = new(staged, length, jpeg.Width, jpeg.Height, "unchanged");

        // 1. Lossless Huffman / progressive recompression: identical pixels.
        if (await _encoders.RecompressJpegLosslessAsync(staged, _token).ConfigureAwait(false) is { } lossless)
        {
            var size = new FileInfo(lossless.Value).Length;
            var reread = JpegInfo.TryRead(await File.ReadAllBytesAsync(lossless.Value, _token).ConfigureAwait(false));
            if (size < best.Size && reread is { } r && r.Components == components && r.Width == jpeg.Width && r.Height == jpeg.Height)
            {
                best = new ImageCandidateFile(lossless, size, jpeg.Width, jpeg.Height, "lossless");
            }
        }

        // 2. Perceptual re-encode (and downsample), unless a colour-sensitive detail makes pixel access unsafe.
        var orientation = JpegSegments.Extract(bytes).Exif is { Length: > 0 } exif ? JpegSegments.ReadOrientation(exif) : 1;
        var rawRgbCoded = jpeg.Components == 3 && jpeg.AdobeTransform == 0;
        if (orientation == 1 && !rawRgbCoded)
        {
            var scale = ScaleFor(usage, width, height);
            // The pipeline replaces its input in place, so it gets its own copy and the lossless candidate stays intact.
            var lossyInput = _temp.New("squish-pdfimg-lossy", "jpg");
            File.Copy(staged.Value, lossyInput.Value, overwrite: true);
            var lossy = await RunPipelineAsync(lossyInput, convertPng: false, scale, jpeg.Width, jpeg.Height, components, minPixels: 0).ConfigureAwait(false);
            if (lossy is { } candidate && candidate.Size < best.Size)
            {
                best = candidate with { Description = scale is null ? "re-encoded" : "downsampled" };
            }
        }

        Log.Debug($"PDF picture {id}: {jpeg.Width}x{jpeg.Height}, best {best.Description} {best.Size} of {length} bytes, usage {(usage is null ? "none" : usage.Unknown ? "unknown" : usage.Placements.Count + " placements")}");
        if (best.Description == "unchanged" || best.Size >= length * 0.97)
        {
            return null;
        }

        return BuildUpdate(dict, best, length);
    }

    private async Task<ImageUpdate?> OptimiseRawBitmapAsync(int id, JsonElement dict, string file, long length, int width, int height, int components, ImageUsage? usage)
    {
        if ((long)width * height < 20_000)
        {
            return null;
        }

        var staged = _temp.New("squish-pdfbmp", "png");
        var raw = await File.ReadAllBytesAsync(file, _token).ConfigureAwait(false);
        await Task.Run(() =>
        {
            ImageIO.EnsureConfigured();
            var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, components == 3 ? "RGB" : "I");
            using var image = new MagickImage();
            image.ReadPixels(raw, settings);
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
            image.Format = MagickFormat.Png;
            image.Write(staged.Value);
        }, _token).ConfigureAwait(false);

        var scale = ScaleFor(usage, width, height);
        var converted = await RunPipelineAsync(staged, convertPng: true, scale, width, height, components, minPixels: 20_000).ConfigureAwait(false);

        // The Flate original is already deflated by the structural pass; the JPEG has to clearly beat it to be worth the loss.
        var flateSize = _structure.ValueOf(id) is { } original ? PdfObjectMap.Int(original, "/Length") ?? 0 : 0;
        var reference = flateSize > 0 ? flateSize : length / 2;
        if (converted is not { } candidate || candidate.Size >= reference * 0.9)
        {
            return null;
        }

        return BuildUpdate(dict, candidate with { Description = "photographic bitmap converted to JPEG" }, reference);
    }

    private sealed record ImageCandidateFile(FilePath File, long Size, int Width, int Height, string Description);

    /// <summary>Runs the shared image pipeline on a staged picture and returns the JPEG it ships, or null when nothing acceptable came out.</summary>
    private async Task<ImageCandidateFile?> RunPipelineAsync(FilePath staged, bool convertPng, double? scale, int width, int height, int components, long minPixels)
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
            [OptimisationMetadata.ImageAggressive] = _aggressive
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
        if (extension is not ("jpg" or "jpeg"))
        {
            return null;
        }

        var current = output;
        var data = await File.ReadAllBytesAsync(current.Value, _token).ConfigureAwait(false);
        var info = JpegInfo.TryRead(data);
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
            data = await File.ReadAllBytesAsync(current.Value, _token).ConfigureAwait(false);
            info = JpegInfo.TryRead(data);
            if (info is not { } greyInfo)
            {
                return null;
            }

            jpeg = greyInfo;
        }

        return jpeg.Components == components ? new ImageCandidateFile(current, data.LongLength, jpeg.Width, jpeg.Height, "re-encoded") : null;
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

    private ImageUpdate BuildUpdate(JsonElement dict, ImageCandidateFile best, long originalSize)
    {
        var node = JsonNode.Parse(dict.GetRawText())!.AsObject();
        node["/Filter"] = "/DCTDecode";
        node.Remove("/DecodeParms");
        node.Remove("/Length");
        node["/BitsPerComponent"] = 8;
        node["/Width"] = best.Width;
        node["/Height"] = best.Height;

        // The temp scope disposes with the pass, which is after the update has been applied.
        var stream = new JsonObject { ["dict"] = node, ["datafile"] = best.File.Value };
        return new ImageUpdate(new JsonObject { ["stream"] = stream }, originalSize - best.Size);
    }

    // ------------------------------------------------------------------------------------------------------
    // Metadata that lives in object dictionaries
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Page thumbnails (/Thumb) and application-private data (/PieceInfo) on pages and the catalog.</summary>
    private IEnumerable<(string Key, JsonNode Node)> BuildMetadataUpdates()
    {
        foreach (var id in _structure.Ids)
        {
            if (_structure.IsStream(id) || _structure.ValueOf(id) is not { ValueKind: JsonValueKind.Object } value)
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
        }
    }
}
