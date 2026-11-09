using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Squish.Core.Optimizers;
using Squish.Core.Pdf;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>Picture-handling behaviour of the PDF optimiser beyond the basic JPEG / bitmap round trip.</summary>
public sealed class PdfImagePassTests
{
    private static readonly PdfOptimiserOptions BuiltIn = new()
    {
        ImageOptions = new ImageOptimiserOptions { AdvancedCodecs = AdvancedCodecPreferences.Disabled }
    };

    private static readonly IReadOnlyDictionary<string, object?> ReplaceInPlace = new Dictionary<string, object?>
    {
        [OptimisationMetadata.OutputReplaceOriginal] = true
    };

    // ---- pure helpers (no external tools) ------------------------------------------------------------------

    [Fact]
    public void PngPdfStreamReadsPlainRgbGreyAndPaletteFiles()
    {
        var rgba = TestImages.GraphicRgba(64, 48);

        var rgb = PngPdfStream.TryRead(WritePng(rgba, 64, 48, "png:color-type=2"));
        var palette = PngPdfStream.TryRead(WritePng(rgba, 64, 48, "png:color-type=3"));
        var grey = PngPdfStream.TryRead(WritePng(rgba, 64, 48, "png:color-type=0"));

        Assert.NotNull(rgb);
        Assert.Equal(2, rgb!.ColorType);
        Assert.Equal(3, rgb.Colors);
        Assert.Equal(8, rgb.BitDepth);
        Assert.Null(rgb.Palette);
        // The IDAT payload is a zlib stream of filtered rows: one filter byte plus the row's pixels each.
        Assert.Equal(48 * (1 + 64 * 3), Inflate(rgb.Idat).Length);

        Assert.NotNull(palette);
        Assert.Equal(3, palette!.ColorType);
        Assert.NotNull(palette.Palette);
        Assert.InRange(palette.PaletteEntries, 2, 256);

        Assert.NotNull(grey);
        Assert.Equal(0, grey!.ColorType);
        Assert.Equal(1, grey.Colors);
    }

    [Fact]
    public void PngPdfStreamRejectsWhatAPdfCannotCarryUnchanged()
    {
        var rgba = TestImages.GraphicRgba(64, 48);
        var withAlpha = TestImages.GraphicRgba(64, 48, transparentBackground: true);

        Assert.Null(PngPdfStream.TryRead(WritePng(withAlpha, 64, 48, "png:color-type=6")));
        Assert.Null(PngPdfStream.TryRead(WritePng(rgba, 64, 48, "png:color-type=2", interlace: true)));
        Assert.Null(PngPdfStream.TryRead(WritePng(rgba, 64, 48, "png:color-type=2", bitDepth: 16)));
        Assert.Null(PngPdfStream.TryRead(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }));
        Assert.Null(PngPdfStream.TryRead(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void PaletteBecomesAnIndexedSpaceOnTopOfTheOriginalColourSpace()
    {
        var png = new PngPdfStream(10, 10, 4, 3, [255, 0, 0, 0, 255, 0, 0, 0, 255], [1, 2, 3]);

        using var device = JsonDocument.Parse("""{ "/ColorSpace": "/DeviceRGB" }""");
        using var wrapped = JsonDocument.Parse("""{ "/ColorSpace": ["/DeviceRGB"] }""");
        using var icc = JsonDocument.Parse("""{ "/ColorSpace": ["/ICCBased", "9 0 R"] }""");

        AssertJson("[\"/Indexed\",\"/DeviceRGB\",2,\"b:FF000000FF000000FF\"]", PdfImagePass.MapPngColour(device.RootElement, png, 3));
        AssertJson("[\"/Indexed\",\"/DeviceRGB\",2,\"b:FF000000FF000000FF\"]", PdfImagePass.MapPngColour(wrapped.RootElement, png, 3));
        AssertJson("[\"/Indexed\",[\"/ICCBased\",\"9 0 R\"],2,\"b:FF000000FF000000FF\"]", PdfImagePass.MapPngColour(icc.RootElement, png, 3));
        // A colour palette cannot stand in for a grey picture.
        Assert.Null(PdfImagePass.MapPngColour(device.RootElement, png, 1));
    }

    [Fact]
    public void GreyScanlinesRelabelOnlyDeviceRgbAndNeverAnIccProfile()
    {
        var grey = new PngPdfStream(10, 10, 8, 0, null, [1, 2, 3]);

        using var device = JsonDocument.Parse("""{ "/ColorSpace": "/DeviceRGB" }""");
        using var icc = JsonDocument.Parse("""{ "/ColorSpace": ["/ICCBased", "9 0 R"] }""");
        using var greySpace = JsonDocument.Parse("""{ "/ColorSpace": "/DeviceGray" }""");

        Assert.Equal("\"/DeviceGray\"", PdfImagePass.MapPngColour(device.RootElement, grey, 3));
        Assert.Null(PdfImagePass.MapPngColour(icc.RootElement, grey, 3));
        Assert.Equal("\"/DeviceGray\"", PdfImagePass.MapPngColour(greySpace.RootElement, grey, 1));
    }

    [Fact]
    public void DecodeArraysAreIdentityOnlyWhenTheyListTheDefaultRange()
    {
        using var document = JsonDocument.Parse("""{ "rgb": [0, 1, 0, 1, 0, 1], "grey": [0, 1], "inverted": [1, 0], "short": [0, 1, 0, 1], "scaled": [0, 0.5] }""");
        var root = document.RootElement;

        Assert.True(PdfImagePass.IsIdentityDecode(root.GetProperty("rgb"), 3));
        Assert.True(PdfImagePass.IsIdentityDecode(root.GetProperty("grey"), 1));
        Assert.False(PdfImagePass.IsIdentityDecode(root.GetProperty("inverted"), 1));
        Assert.False(PdfImagePass.IsIdentityDecode(root.GetProperty("short"), 3));
        Assert.False(PdfImagePass.IsIdentityDecode(root.GetProperty("scaled"), 1));
    }

    [Fact]
    public void JpegDecodeParmsAreDefaultForColorTransformOneOnYcbcrAndNothingElse()
    {
        var ycbcr = new JpegInfo(10, 10, 3, -1, false);
        var adobeRgb = new JpegInfo(10, 10, 3, 0, false);
        var grey = new JpegInfo(10, 10, 1, -1, false);

        static JsonElement Dict(string json) => JsonDocument.Parse(json).RootElement;

        Assert.True(PdfImagePass.JpegDecodeParmsAreDefault(Dict("{}"), ycbcr));
        Assert.True(PdfImagePass.JpegDecodeParmsAreDefault(Dict("""{ "/DecodeParms": { "/ColorTransform": 1 } }"""), ycbcr));
        Assert.True(PdfImagePass.JpegDecodeParmsAreDefault(Dict("""{ "/DecodeParms": [ { "/ColorTransform": 1 } ] }"""), ycbcr));
        Assert.True(PdfImagePass.JpegDecodeParmsAreDefault(Dict("""{ "/DecodeParms": {} }"""), ycbcr));
        Assert.True(PdfImagePass.JpegDecodeParmsAreDefault(Dict("""{ "/DecodeParms": [null] }"""), ycbcr));
        Assert.True(PdfImagePass.JpegDecodeParmsAreDefault(Dict("""{ "/DecodeParms": { "/ColorTransform": 0 } }"""), grey));

        Assert.False(PdfImagePass.JpegDecodeParmsAreDefault(Dict("""{ "/DecodeParms": { "/ColorTransform": 0 } }"""), ycbcr));
        Assert.False(PdfImagePass.JpegDecodeParmsAreDefault(Dict("""{ "/DecodeParms": { "/ColorTransform": 1 } }"""), adobeRgb));
        Assert.False(PdfImagePass.JpegDecodeParmsAreDefault(Dict("""{ "/DecodeParms": { "/Predictor": 15 } }"""), ycbcr));
    }

    [Fact]
    public void SingleElementFilterArraysReadLikeTheBareName()
    {
        using var document = JsonDocument.Parse("""{ "bare": { "/Filter": "/DCTDecode" }, "array": { "/Filter": ["/DCTDecode"] }, "chain": { "/Filter": ["/FlateDecode", "/DCTDecode"] }, "empty": { "/Filter": [] }, "none": {} }""");
        var root = document.RootElement;

        Assert.Equal("/DCTDecode", PdfObjectMap.SingleFilter(root.GetProperty("bare")));
        Assert.Equal("/DCTDecode", PdfObjectMap.SingleFilter(root.GetProperty("array")));
        Assert.Null(PdfObjectMap.SingleFilter(root.GetProperty("chain")));
        Assert.True(PdfObjectMap.HasFilter(root.GetProperty("chain")));
        Assert.False(PdfObjectMap.HasFilter(root.GetProperty("empty")));
        Assert.False(PdfObjectMap.HasFilter(root.GetProperty("none")));
    }

    // ---- full pipeline (needs qpdf) ------------------------------------------------------------------------

    [ToolFact("qpdf")]
    public async Task ScreenshotBitmapBecomesAnIndexedPicture()
    {
        var rgba = TestImages.GraphicRgba(800, 600);
        var source = new PdfBuilder().Build(
            "q 400 0 0 300 72 400 cm /Im1 Do Q",
            "/Im1 5 0 R",
            [($"/Type /XObject /Subtype /Image /Width 800 /Height 600 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", Deflate(ToRgb(rgba), CompressionLevel.Fastest))]);
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var newSize = new FileInfo(source.Value).Length;
            Assert.True(newSize < originalSize * 0.8, $"{originalSize} -> {newSize}: {result.Message}");
            await AssertValidAsync(source.Value);

            using var objects = await ReadDictsAsync(source.Value);
            var image = objects.Images.Single();
            Assert.Equal("/FlateDecode", PdfObjectMap.SingleFilter(image.Dict));
            // The screenshot has a handful of colours: it becomes an indexed picture. Whether the scanlines keep their PNG
            // predictor depends on which of the two rewrites came out smaller, so either is correct.
            var space = image.Dict.GetProperty("/ColorSpace");
            Assert.True(space.ValueKind == JsonValueKind.Array && space[0].GetString() == "/Indexed", $"{result.Message}: {image.Dict.GetRawText()}");
            Assert.True(image.Dict.GetProperty("/BitsPerComponent").GetInt32() <= 8);

            // The scanlines decode cleanly: the row count and width add up to exactly what the colour space needs.
            var decoded = await DecodeAsync(source.Value, image.Id);
            var bits = image.Dict.GetProperty("/BitsPerComponent").GetInt32();
            var colors = image.Dict.GetProperty("/ColorSpace").ValueKind == JsonValueKind.Array && image.Dict.GetProperty("/ColorSpace")[0].GetString() == "/Indexed"
                ? 1
                : image.Dict.GetProperty("/ColorSpace").GetString() == "/DeviceGray" ? 1 : 3;
            var width = image.Dict.GetProperty("/Width").GetInt32();
            var height = image.Dict.GetProperty("/Height").GetInt32();
            Assert.Equal((long)height * ((width * bits * colors + 7) / 8), decoded.Length);
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [ToolFact("qpdf")]
    public async Task JpegWithFilterArrayExplicitDefaultsAndCalibratedColourIsStillOptimised()
    {
        var jpeg = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(2400, 1800, seed: 3), 2400, 1800, quality: 97);
        var data = File.ReadAllBytes(jpeg.Value);
        var source = new PdfBuilder().Build(
            "q 288 0 0 216 72 450 cm /Im1 Do Q",
            "/Im1 5 0 R",
            [("/Type /XObject /Subtype /Image /Width 2400 /Height 1800 /ColorSpace [/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [2.2 2.2 2.2] >>] /BitsPerComponent 8 /Filter [/DCTDecode] /DecodeParms << /ColorTransform 1 >> /Decode [0 1 0 1 0 1]", data)]);
        try
        {
            await RunAsync(source, ReplaceInPlace);

            using var objects = await ReadDictsAsync(source.Value);
            var image = objects.Images.Single();
            // 4 inches at 220 dpi = 880 px; the picture was 600 dpi.
            Assert.InRange(image.Dict.GetProperty("/Width").GetInt32(), 850, 920);
            Assert.Equal("/CalRGB", image.Dict.GetProperty("/ColorSpace")[0].GetString());
        }
        finally
        {
            TestImages.TryDelete(source, jpeg);
        }
    }

    [ToolFact("qpdf")]
    public async Task UserUnitScalesThePlacementSoPicturesAreNotOverDownsampled()
    {
        var jpeg = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(2400, 1800, seed: 6), 2400, 1800, quality: 97);
        var data = File.ReadAllBytes(jpeg.Value);

        // 288 x 216 user units at UserUnit 4 is 16 x 12 inches: the 2400 px picture is only 150 dpi, so it keeps its size.
        var source = new PdfBuilder().Build(
            "q 288 0 0 216 72 450 cm /Im1 Do Q",
            "/Im1 5 0 R",
            [("/Type /XObject /Subtype /Image /Width 2400 /Height 1800 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", data)],
            pageExtras: "/UserUnit 4");
        try
        {
            await RunAsync(source, ReplaceInPlace);

            using var objects = await ReadDictsAsync(source.Value);
            Assert.Equal(2400, objects.Images.Single().Dict.GetProperty("/Width").GetInt32());
        }
        finally
        {
            TestImages.TryDelete(source, jpeg);
        }
    }

    [ToolFact("qpdf")]
    public async Task SoftMaskShrinksWithItsPictureAndStaysLossless()
    {
        var jpeg = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(2400, 1800, seed: 8), 2400, 1800, quality: 96);
        var alpha = new byte[2400 * 1800];
        for (var y = 0; y < 1800; y++)
        {
            for (var x = 0; x < 2400; x++)
            {
                var dx = (x - 1200) / 1200.0;
                var dy = (y - 900) / 900.0;
                alpha[y * 2400 + x] = (byte)Math.Clamp(255 * (1.2 - Math.Sqrt(dx * dx + dy * dy)), 0, 255);
            }
        }

        var maskData = Deflate(alpha, CompressionLevel.Fastest);
        var source = new PdfBuilder().Build(
            "q 288 0 0 216 72 450 cm /Im1 Do Q",
            "/Im1 5 0 R",
            [
                ("/Type /XObject /Subtype /Image /Width 2400 /Height 1800 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /SMask 6 0 R", File.ReadAllBytes(jpeg.Value)),
                ("/Type /XObject /Subtype /Image /Width 2400 /Height 1800 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", maskData)
            ]);
        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            await AssertValidAsync(source.Value);
            using var objects = await ReadDictsAsync(source.Value);
            var picture = objects.Images.Single(i => i.Dict.TryGetProperty("/SMask", out _));
            var mask = objects.Images.Single(i => !i.Dict.TryGetProperty("/SMask", out _));

            Assert.InRange(picture.Dict.GetProperty("/Width").GetInt32(), 850, 920);
            Assert.InRange(mask.Dict.GetProperty("/Width").GetInt32(), 850, 920);
            Assert.Equal("/FlateDecode", PdfObjectMap.SingleFilter(mask.Dict));
            Assert.Equal(15, mask.Dict.GetProperty("/DecodeParms").GetProperty("/Predictor").GetInt32());
            Assert.True(mask.Dict.GetProperty("/Length").GetInt64() < maskData.Length / 4, $"mask {mask.Dict.GetProperty("/Length").GetInt64()} vs {maskData.Length}");
        }
        finally
        {
            TestImages.TryDelete(source, jpeg);
        }
    }

    [ToolFact("qpdf")]
    public async Task DuplicatePicturesAreAllOptimisedAndStayValid()
    {
        var jpeg = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(2000, 1500, seed: 11), 2000, 1500, quality: 97);
        var data = File.ReadAllBytes(jpeg.Value);
        const string Dict = "/Type /XObject /Subtype /Image /Width 2000 /Height 1500 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode";
        var source = new PdfBuilder().Build(
            "q 288 0 0 216 72 450 cm /Im1 Do Q q 288 0 0 216 72 100 cm /Im2 Do Q",
            "/Im1 5 0 R /Im2 6 0 R",
            [(Dict, data), (Dict, data)]);
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            await RunAsync(source, ReplaceInPlace);

            await AssertValidAsync(source.Value);
            Assert.True(new FileInfo(source.Value).Length < originalSize * 0.3);
            using var objects = await ReadDictsAsync(source.Value);
            Assert.Equal(2, objects.Images.Count);
            Assert.All(objects.Images, i => Assert.InRange(i.Dict.GetProperty("/Width").GetInt32(), 850, 920));
        }
        finally
        {
            TestImages.TryDelete(source, jpeg);
        }
    }

    [ToolFact("qpdf")]
    public async Task PrivateDataOnFormAndPictureObjectsIsStrippedWithoutTouchingTheirContent()
    {
        var filler = string.Concat(Enumerable.Repeat("0.5 0.5 0.5 rg 10 10 80 80 re f\n", 1500));
        var source = new PdfBuilder().Build(
            "/Fm1 Do",
            "/Fm1 5 0 R",
            [("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /PieceInfo << /Illustrator << /Private (SECRETSAUCE) >> >> /Metadata 6 0 R", Encoding.Latin1.GetBytes(filler + "0 1 0 rg 0 0 5 5 re f")),
             ("/Type /Metadata /Subtype /XML", Encoding.Latin1.GetBytes("<x:xmpmeta>PRIVATEXMP</x:xmpmeta>"))]);
        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            await AssertValidAsync(source.Value);
            using var objects = await ReadDictsAsync(source.Value);
            var form = objects.Streams.Single(s => s.Dict.TryGetProperty("/Subtype", out var t) && t.GetString() == "/Form");
            Assert.False(form.Dict.TryGetProperty("/PieceInfo", out _));
            Assert.False(form.Dict.TryGetProperty("/Metadata", out _));

            var data = Encoding.Latin1.GetString(await DecodeAsync(source.Value, form.Id));
            Assert.EndsWith("0 1 0 rg 0 0 5 5 re f", data);
            var qdf = FilePath.TempFile("squish-qdf", "pdf", addUniqueSuffix: true);
            try
            {
                await Qpdf("--qdf", "--object-streams=disable", source.Value, qdf.Value);
                var text = Encoding.Latin1.GetString(File.ReadAllBytes(qdf.Value));
                Assert.DoesNotContain("SECRETSAUCE", text);
                Assert.DoesNotContain("PRIVATEXMP", text);
            }
            finally
            {
                TestImages.TryDelete(qdf);
            }
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [ToolFact("qpdf")]
    public async Task ExistingPredictorCodedPicturesSurviveTheRewrite()
    {
        // A small picture is below the optimiser's threshold, so it is only carried across. Uncompressed text gives the
        // rewrite something to save, which makes the output replace the source.
        var rgb = ToRgb(TestImages.PhotoLikeRgba(96, 96, seed: 2));
        var predicted = Deflate(PngSubFilter(rgb, 96, 96), CompressionLevel.SmallestSize);
        var filler = string.Concat(Enumerable.Repeat("BT /F1 12 Tf 72 700 Td (predictors matter) Tj ET\n", 800));
        var source = new PdfBuilder().Build(
            filler + "q 96 0 0 96 72 400 cm /Im1 Do Q",
            "/Im1 5 0 R",
            [("/Type /XObject /Subtype /Image /Width 96 /Height 96 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors 3 /BitsPerComponent 8 /Columns 96 >>", predicted)]);
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            await RunAsync(source, ReplaceInPlace);

            await AssertValidAsync(source.Value);
            Assert.True(new FileInfo(source.Value).Length < originalSize);
            using var objects = await ReadDictsAsync(source.Value);
            var image = objects.Images.Single();
            Assert.Equal(15, image.Dict.GetProperty("/DecodeParms").GetProperty("/Predictor").GetInt32());
            Assert.True(image.Dict.GetProperty("/Length").GetInt64() <= predicted.Length + 16);
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static void AssertJson(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(actual!)), $"expected {expected}, got {actual}");
    }

    private static byte[] WritePng(byte[] rgba, int width, int height, string colorTypeDefine, bool interlace = false, int bitDepth = 8)
    {
        using var image = new MagickImage();
        image.ReadPixels(rgba, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        if (colorTypeDefine != "png:color-type=6")
        {
            image.Alpha(AlphaAction.Off);
        }

        image.Depth = (uint)bitDepth;
        if (colorTypeDefine == "png:color-type=0")
        {
            image.ColorType = ColorType.Grayscale;
        }

        var parts = colorTypeDefine.Split('=');
        image.Settings.SetDefine(MagickFormat.Png, "color-type", parts[1]);
        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", bitDepth.ToString());
        if (interlace)
        {
            image.Settings.Interlace = Interlace.Png;
        }

        image.Format = MagickFormat.Png;
        using var stream = new MemoryStream();
        image.Write(stream);
        return stream.ToArray();
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var input = new MemoryStream(zlib);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    internal static byte[] Deflate(byte[] data, CompressionLevel level)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, level, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }

    internal static byte[] ToRgb(byte[] rgba)
    {
        var rgb = new byte[rgba.Length / 4 * 3];
        for (var i = 0; i < rgba.Length / 4; i++)
        {
            rgb[i * 3] = rgba[i * 4];
            rgb[i * 3 + 1] = rgba[i * 4 + 1];
            rgb[i * 3 + 2] = rgba[i * 4 + 2];
        }

        return rgb;
    }

    /// <summary>PNG "Sub" filtering of 8-bit RGB rows, the form /Predictor 15 expects.</summary>
    private static byte[] PngSubFilter(byte[] rgb, int width, int height)
    {
        var stride = width * 3;
        var result = new byte[height * (stride + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            result[row] = 1;
            for (var i = 0; i < stride; i++)
            {
                var left = i >= 3 ? rgb[y * stride + i - 3] : 0;
                result[row + 1 + i] = (byte)(rgb[y * stride + i] - left);
            }
        }

        return result;
    }

    private static Task<OptimisationResult> RunAsync(FilePath source, IReadOnlyDictionary<string, object?>? metadata)
    {
        var request = new OptimisationRequest(ItemType.Pdf, source, metadata: metadata);
        var context = new OptimiserExecutionContext(request.RequestId, _ => { });
        return new PdfOptimiser(BuiltIn).OptimiseAsync(request, context, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(4));
    }

    private static async Task AssertValidAsync(string pdf)
    {
        var check = await Qpdf("--check", pdf);
        Assert.True(check.ExitCode is 0 or 3, check.Output);
    }

    private sealed record StreamInfo(int Id, JsonElement Dict);

    private sealed class Objects : IDisposable
    {
        private readonly JsonDocument _document;

        public Objects(JsonDocument document, List<StreamInfo> streams)
        {
            _document = document;
            Streams = streams;
        }

        public List<StreamInfo> Streams { get; }

        public List<StreamInfo> Images => Streams.Where(s => s.Dict.TryGetProperty("/Subtype", out var t) && t.GetString() == "/Image").ToList();

        public void Dispose() => _document.Dispose();
    }

    private static async Task<Objects> ReadDictsAsync(string pdf)
    {
        var json = FilePath.TempFile("squish-pdf-json", "json", addUniqueSuffix: true);
        try
        {
            var run = await Qpdf(pdf, json.Value, "--json-output", "--json-stream-data=none");
            Assert.True(run.ExitCode is 0 or 3, run.Output);
            var document = JsonDocument.Parse(File.ReadAllBytes(json.Value));
            var streams = new List<StreamInfo>();
            foreach (var property in document.RootElement.GetProperty("qpdf")[1].EnumerateObject())
            {
                if (property.Value.TryGetProperty("stream", out var stream) && stream.TryGetProperty("dict", out var dict) && PdfObjectMap.TryParseObjectKey(property.Name, out var id))
                {
                    streams.Add(new StreamInfo(id, dict));
                }
            }

            return new Objects(document, streams);
        }
        finally
        {
            TestImages.TryDelete(json);
        }
    }

    /// <summary>The stream's data with every filter (including predictors) decoded.</summary>
    private static async Task<byte[]> DecodeAsync(string pdf, int id)
    {
        var directory = Path.Combine(Path.GetTempPath(), "squish-pdftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var json = Path.Combine(directory, "o.json");
            var run = await Qpdf(pdf, json, "--json-output", "--json-stream-data=file", "--decode-level=generalized", $"--json-stream-prefix={Path.Combine(directory, "s-")}", $"--json-object={id}");
            Assert.True(run.ExitCode is 0 or 3, run.Output);
            using var document = JsonDocument.Parse(File.ReadAllBytes(json));
            var file = document.RootElement.GetProperty("qpdf")[1].GetProperty($"obj:{id} 0 R").GetProperty("stream").GetProperty("datafile").GetString()!;
            return File.ReadAllBytes(file);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> Qpdf(params string[] arguments)
    {
        var info = new ProcessStartInfo(ToolAvailability.Qpdf!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }
}

/// <summary>Builds small single-page PDFs: object 1 is the catalog, 2 the page tree, 3 the page, 4 its content; extra streams start at 5.</summary>
internal sealed class PdfBuilder
{
    public FilePath Build(string content, string xobjects, IEnumerable<(string Dictionary, byte[] Data)> streams, string pageExtras = "")
    {
        var path = FilePath.TempFile("squish-test-pdf", "pdf", addUniqueSuffix: true);
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {pageExtras} /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> /XObject << {xobjects} >> >> /Contents 4 0 R >>"),
            StreamObject(string.Empty, Ascii(content))
        };
        foreach (var (dictionary, data) in streams)
        {
            objects.Add(StreamObject(dictionary, data));
        }

        using var file = new MemoryStream();
        file.Write(Ascii("%PDF-1.7\n%âãÏÓ\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            file.Write(Ascii($"{i + 1} 0 obj\n"));
            file.Write(objects[i]);
            file.Write(Ascii("\nendobj\n"));
        }

        var xref = file.Position;
        file.Write(Ascii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
        {
            file.Write(Ascii($"{offset:D10} 00000 n \n"));
        }

        file.Write(Ascii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        File.WriteAllBytes(path.Value, file.ToArray());
        return path;
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] StreamObject(string dictionary, byte[] data)
    {
        using var memory = new MemoryStream();
        memory.Write(Ascii($"<< {dictionary} /Length {data.Length} >>\nstream\n"));
        memory.Write(data);
        memory.Write(Ascii("\nendstream"));
        return memory.ToArray();
    }
}
