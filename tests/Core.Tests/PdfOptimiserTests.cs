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
using Squish.Core.Optimizers;
using Squish.Core.Pdf;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class PdfOptimiserTests
{
    private static readonly PdfOptimiserOptions BuiltIn = new()
    {
        ImageOptions = new ImageOptimiserOptions { AdvancedCodecs = AdvancedCodecPreferences.Disabled }
    };

    private static readonly IReadOnlyDictionary<string, object?> ReplaceInPlace = new Dictionary<string, object?>
    {
        [OptimisationMetadata.OutputReplaceOriginal] = true
    };

    // ---- content scanner (no external tools) ---------------------------------------------------------------

    [Fact]
    public void ScannerReportsPlacementSizesThroughTheGraphicsStateStack()
    {
        var content = "q 288 0 0 216 100 100 cm /Im1 Do Q q 0.5 0 0 0.5 0 0 cm 144 0 0 108 0 0 cm /Im2 Do Q (a string with Do and q) Tj /Im3 Do";
        var found = new List<(string Name, double Width, double Height)>();

        PdfContentScanner.Scan(Encoding.Latin1.GetBytes(content), PdfMatrix.Identity, (name, ctm) =>
        {
            var (w, h) = ctm.UnitSquareSize;
            found.Add((name, w, h));
        });

        Assert.Equal(3, found.Count);
        Assert.Equal(("Im1", 288.0, 216.0), found[0]);
        Assert.Equal(("Im2", 72.0, 54.0), found[1]);
        Assert.Equal(("Im3", 1.0, 1.0), found[2]);
    }

    [Fact]
    public void ScannerStepsOverInlineImagesAndHandlesRotation()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.Latin1.GetBytes("q 0 100 -50 0 0 0 cm /Rotated Do Q BI /W 2 /H 2 /BPC 8 /CS /G ID "));
        bytes.AddRange(new byte[] { 0x45, 0x49, 0xFF, 0x00 }); // pixel data that happens to contain "EI"
        bytes.AddRange(Encoding.Latin1.GetBytes("\nEI\nq 10 0 0 10 0 0 cm /After Do Q"));
        var found = new List<(string Name, double Width, double Height)>();

        PdfContentScanner.Scan(bytes.ToArray(), PdfMatrix.Identity, (name, ctm) =>
        {
            var (w, h) = ctm.UnitSquareSize;
            found.Add((name, w, h));
        });

        Assert.Equal(2, found.Count);
        Assert.Equal(("Rotated", 100.0, 50.0), found[0]);
        Assert.Equal(("After", 10.0, 10.0), found[1]);
    }

    [Fact]
    public void JpegInfoReadsDimensionsComponentsAndAdobeMarker()
    {
        var pixels = TestImages.PhotoLikeRgba(96, 64);
        var jpeg = TestImages.SaveJpeg(pixels, 96, 64, quality: 90);
        try
        {
            var info = JpegInfo.TryRead(File.ReadAllBytes(jpeg.Value));

            Assert.NotNull(info);
            Assert.Equal(96, info!.Value.Width);
            Assert.Equal(64, info.Value.Height);
            Assert.Equal(3, info.Value.Components);
            Assert.False(info.Value.HasAdobeMarker);
            Assert.Null(JpegInfo.TryRead(new byte[] { 1, 2, 3, 4, 5 }));
        }
        finally
        {
            TestImages.TryDelete(jpeg);
        }
    }

    // ---- full pipeline (needs qpdf) -----------------------------------------------------------------------

    [ToolFact("qpdf")]
    public async Task DownsamplesOversizedJpegConvertsBitmapStripsMetadataAndKeepsText()
    {
        var photo = TestImages.PhotoLikeRgba(2400, 1800);
        var jpeg = TestImages.SaveJpeg(photo, 2400, 1800, quality: 97);
        var bitmap = TestImages.PhotoLikeRgba(640, 480, seed: 5);
        var source = PdfFixtures.Create(File.ReadAllBytes(jpeg.Value), 2400, 1800, bitmap, 640, 480);
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var newSize = new FileInfo(source.Value).Length;
            Assert.True(newSize < originalSize * 0.3, $"{originalSize} -> {newSize}: {result.Message}");

            // The file is still a valid PDF.
            var check = await Qpdf("--check", source.Value);
            Assert.True(check.ExitCode is 0 or 3, check.Output);

            // Text and the structure around it are exactly as before.
            var qdf = FilePath.TempFile("squish-qdf", "pdf", addUniqueSuffix: true);
            await Qpdf("--qdf", "--object-streams=disable", source.Value, qdf.Value);
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(qdf.Value));
            TestImages.TryDelete(qdf);
            Assert.Contains("(Hello PDF) Tj", text);
            Assert.Contains("/BaseFont /Helvetica", text);
            Assert.DoesNotContain("Jane Secret", text);

            // 4 inches at 220 dpi = 880 px; the JPEG was 2400 px (600 dpi). The bitmap became a JPEG.
            var images = await ReadImagesAsync(source.Value);
            Assert.Equal(2, images.Count);
            var big = images.Single(i => i.Width < 2000 && i.Width >= 800 && i.Filter == "/DCTDecode");
            Assert.InRange(big.Width, 850, 920);
            Assert.Equal(2400.0 / 1800.0, (double)big.Width / big.Height, 2);
            Assert.Contains(images, i => i.Width == 640 && i.Filter == "/DCTDecode");
        }
        finally
        {
            TestImages.TryDelete(source, jpeg);
        }
    }

    [ToolFact("qpdf")]
    public async Task AggressiveModeDownsamplesHarder()
    {
        var photo = TestImages.PhotoLikeRgba(2400, 1800, seed: 9);
        var jpeg = TestImages.SaveJpeg(photo, 2400, 1800, quality: 97);
        var source = PdfFixtures.Create(File.ReadAllBytes(jpeg.Value), 2400, 1800, null, 0, 0);
        try
        {
            var metadata = new Dictionary<string, object?>
            {
                [OptimisationMetadata.OutputReplaceOriginal] = true,
                [OptimisationMetadata.PdfAggressive] = true
            };
            await RunAsync(source, metadata);

            var images = await ReadImagesAsync(source.Value);
            Assert.InRange(images.Single().Width, 580, 620); // 4 inches at 150 dpi
        }
        finally
        {
            TestImages.TryDelete(source, jpeg);
        }
    }

    [ToolFact("qpdf")]
    public async Task LeavesSignedAndEncryptedPdfsUntouched()
    {
        var jpeg = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1200, 900), 1200, 900, quality: 97);
        var signed = PdfFixtures.Create(File.ReadAllBytes(jpeg.Value), 1200, 900, null, 0, 0, signature: true);
        var plain = PdfFixtures.Create(File.ReadAllBytes(jpeg.Value), 1200, 900, null, 0, 0);
        var encrypted = FilePath.TempFile("squish-enc", "pdf", addUniqueSuffix: true);
        await Qpdf("--encrypt", "user", "owner", "256", "--", plain.Value, encrypted.Value);
        var signedBytes = File.ReadAllBytes(signed.Value);
        var encryptedBytes = File.ReadAllBytes(encrypted.Value);
        try
        {
            var signedResult = await RunAsync(signed, ReplaceInPlace);
            var encryptedResult = await RunAsync(encrypted, ReplaceInPlace);

            Assert.Contains("signed", signedResult.Message);
            Assert.Equal(signedBytes, File.ReadAllBytes(signed.Value));
            Assert.Contains("Encrypted", encryptedResult.Message);
            Assert.Equal(encryptedBytes, File.ReadAllBytes(encrypted.Value));
        }
        finally
        {
            TestImages.TryDelete(jpeg, signed, plain, encrypted);
        }
    }

    [ToolFact("qpdf")]
    public async Task WritesNextToTheOriginalAndNeverGrowsOnASecondRun()
    {
        var jpeg = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1800, 1350, seed: 4), 1800, 1350, quality: 96);
        var source = PdfFixtures.Create(File.ReadAllBytes(jpeg.Value), 1800, 1350, null, 0, 0);
        var before = File.ReadAllBytes(source.Value);
        FilePath? output = null;
        FilePath? second = null;
        try
        {
            var first = await RunAsync(source, null);
            output = first.OutputPath;
            Assert.NotNull(output);
            Assert.EndsWith(".squish.pdf", output!.Value.Value);
            Assert.Equal(before, File.ReadAllBytes(source.Value));

            var again = await RunAsync(output.Value, ReplaceInPlace);
            second = again.OutputPath;
            Assert.True(new FileInfo(output.Value.Value).Length <= new FileInfo(source.Value).Length);
            Assert.NotNull(second);
        }
        finally
        {
            TestImages.TryDelete(jpeg, source, output, second);
        }
    }

    [Fact]
    public async Task RejectsFilesThatAreNotPdfs()
    {
        var path = FilePath.TempFile("squish-fake", "pdf", addUniqueSuffix: true);
        await File.WriteAllTextAsync(path.Value, "this is not a pdf");
        try
        {
            var result = await RunAsync(path, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Contains("Not a PDF", result.Message);
        }
        finally
        {
            TestImages.TryDelete(path);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static Task<OptimisationResult> RunAsync(FilePath source, IReadOnlyDictionary<string, object?>? metadata)
    {
        var request = new OptimisationRequest(ItemType.Pdf, source, metadata: metadata);
        var context = new OptimiserExecutionContext(request.RequestId, _ => { });
        return new PdfOptimiser(BuiltIn).OptimiseAsync(request, context, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(4));
    }

    private sealed record ImageInfo(int Width, int Height, string? Filter);

    private static async Task<List<ImageInfo>> ReadImagesAsync(string pdf)
    {
        var json = FilePath.TempFile("squish-pdf-json", "json", addUniqueSuffix: true);
        try
        {
            var run = await Qpdf(pdf, json.Value, "--json-output", "--json-stream-data=none");
            Assert.True(run.ExitCode is 0 or 3, run.Output);
            using var document = JsonDocument.Parse(File.ReadAllBytes(json.Value));
            var images = new List<ImageInfo>();
            foreach (var property in document.RootElement.GetProperty("qpdf")[1].EnumerateObject())
            {
                if (!property.Value.TryGetProperty("stream", out var stream) || !stream.TryGetProperty("dict", out var dict))
                {
                    continue;
                }

                if (dict.TryGetProperty("/Subtype", out var subtype) && subtype.GetString() == "/Image")
                {
                    images.Add(new ImageInfo(
                        dict.GetProperty("/Width").GetInt32(),
                        dict.GetProperty("/Height").GetInt32(),
                        dict.TryGetProperty("/Filter", out var filter) ? filter.GetString() : null));
                }
            }

            return images;
        }
        finally
        {
            TestImages.TryDelete(json);
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

/// <summary>Builds a small, valid, single-page PDF with real text, an /Info dictionary and oversized pictures.</summary>
internal static class PdfFixtures
{
    public static FilePath Create(byte[] jpeg, int jpegWidth, int jpegHeight, byte[]? rgba, int bitmapWidth, int bitmapHeight, bool signature = false)
    {
        var path = FilePath.TempFile("squish-test-pdf", "pdf", addUniqueSuffix: true);
        var objects = new List<byte[]>();
        void Add(byte[] data) => objects.Add(data);
        static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

        static byte[] StreamObject(string dictionary, byte[] data)
        {
            using var memory = new MemoryStream();
            memory.Write(Ascii($"<< {dictionary} /Length {data.Length} >>\nstream\n"));
            memory.Write(data);
            memory.Write(Ascii("\nendstream"));
            return memory.ToArray();
        }

        var xobjects = "/Im1 6 0 R" + (rgba is null ? string.Empty : " /Im2 7 0 R");
        var content = "BT /F1 24 Tf 72 720 Td (Hello PDF) Tj ET\nq 288 0 0 216 72 450 cm /Im1 Do Q\n" + (rgba is null ? string.Empty : "q 216 0 0 162 72 240 cm /Im2 Do Q\n");

        Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));                                                                   // 1
        Add(Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"));                                                            // 2
        Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> /XObject << {xobjects} >> >> /Contents 5 0 R >>")); // 3
        Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));                                               // 4
        Add(StreamObject(string.Empty, Ascii(content)));                                                                     // 5
        Add(StreamObject($"/Type /XObject /Subtype /Image /Width {jpegWidth} /Height {jpegHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg)); // 6

        if (rgba is not null)
        {
            var rgb = new byte[bitmapWidth * bitmapHeight * 3];
            for (var i = 0; i < bitmapWidth * bitmapHeight; i++)
            {
                rgb[i * 3] = rgba[i * 4];
                rgb[i * 3 + 1] = rgba[i * 4 + 1];
                rgb[i * 3 + 2] = rgba[i * 4 + 2];
            }

            using var deflated = new MemoryStream();
            using (var z = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(rgb);
            }

            Add(StreamObject($"/Type /XObject /Subtype /Image /Width {bitmapWidth} /Height {bitmapHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", deflated.ToArray())); // 7
        }
        else
        {
            Add(Ascii("<< >>"));
        }

        Add(Ascii("<< /Author (Jane Secret) /Creator (Test Suite) >>"));                                                     // 8
        if (signature)
        {
            Add(Ascii("<< /Type /Sig /Filter /Adobe.PPKLite /ByteRange [0 100 200 100] /Contents <00> >>"));                // 9
        }

        using var file = new MemoryStream();
        file.Write(Ascii("%PDF-1.4\n%âãÏÓ\n"));
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

        file.Write(Ascii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 8 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        File.WriteAllBytes(path.Value, file.ToArray());
        return path;
    }
}
