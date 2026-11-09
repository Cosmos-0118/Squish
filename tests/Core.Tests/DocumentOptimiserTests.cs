using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Squish.Core.Documents;
using Squish.Core.Optimizers;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class DocumentOptimiserTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly DocumentOptimiserOptions BuiltIn = new()
    {
        ImageOptions = new ImageOptimiserOptions { AdvancedCodecs = AdvancedCodecPreferences.Disabled }
    };

    private static readonly IReadOnlyDictionary<string, object?> ReplaceInPlace = new Dictionary<string, object?>
    {
        [OptimisationMetadata.OutputReplaceOriginal] = true
    };

    private static readonly IReadOnlyDictionary<string, object?> Aggressive = new Dictionary<string, object?>
    {
        [OptimisationMetadata.OutputReplaceOriginal] = true,
        [OptimisationMetadata.DocumentAggressive] = true
    };

    // ---- Excel --------------------------------------------------------------------------------------------

    [Fact]
    public async Task RemovesPhantomCellsCalcChainAndScrubsProperties()
    {
        var source = DocumentFixtures.Xlsx(phantomRows: 2000, phantomColumns: 100);
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var path = source.Value;
            var newSize = new FileInfo(path).Length;
            Assert.True(newSize < originalSize * 0.5, $"{originalSize} -> {newSize}");
            DocumentFixtures.AssertValidOpcPackage(path);

            // calcChain is gone from the zip, the workbook rels and the content types.
            Assert.False(DocumentFixtures.HasEntry(path, "xl/calcChain.xml"));
            Assert.DoesNotContain("calcChain", DocumentFixtures.ReadText(path, "xl/_rels/workbook.xml.rels"));
            Assert.DoesNotContain("calcChain", DocumentFixtures.ReadText(path, "[Content_Types].xml"));

            // The thumbnail and its relationship are gone; author details are scrubbed but the title stays.
            Assert.False(DocumentFixtures.HasEntry(path, "docProps/thumbnail.jpeg"));
            Assert.DoesNotContain("thumbnail", DocumentFixtures.ReadText(path, "_rels/.rels"));
            var core = DocumentFixtures.ReadText(path, "docProps/core.xml");
            Assert.DoesNotContain("Secret", core);
            Assert.Contains("Quarterly numbers", core);
            var app = DocumentFixtures.ReadText(path, "docProps/app.xml");
            Assert.DoesNotContain("Secret Corp", app);
            Assert.Contains("Microsoft Excel", app);

            // Data, formulas, the in-range yellow blank and the far-away yellow blank all survive normal mode.
            var sheet = XDocument.Parse(DocumentFixtures.ReadText(path, "xl/worksheets/sheet1.xml"), LoadOptions.PreserveWhitespace);
            var cells = sheet.Descendants(Main + "c").ToList();
            Assert.Equal("B2*2", sheet.Descendants(Main + "f").Skip(1).First().Value);
            Assert.Contains(cells, c => (string?)c.Attribute("r") == "C3" && (string?)c.Attribute("s") == "1");
            Assert.Contains(cells, c => (string?)c.Attribute("r") == "A2005" && (string?)c.Attribute("s") == "1");
            Assert.Equal(5 * 3 + 1, cells.Count);
            Assert.Equal("A1:C2005", (string?)sheet.Descendants(Main + "dimension").Single().Attribute("ref"));

            // Whitespace-only shared strings are content, not indentation.
            var strings = XDocument.Parse(DocumentFixtures.ReadText(path, "xl/sharedStrings.xml"), LoadOptions.PreserveWhitespace);
            Assert.Equal(" ", strings.Descendants(Main + "t").First().Value);
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task AggressiveModeAlsoDropsFormattedBlanksBelowTheData()
    {
        var source = DocumentFixtures.Xlsx(phantomRows: 400, phantomColumns: 20);
        try
        {
            var result = await RunAsync(source, Aggressive);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            DocumentFixtures.AssertValidOpcPackage(source.Value);
            var sheet = XDocument.Parse(DocumentFixtures.ReadText(source.Value, "xl/worksheets/sheet1.xml"), LoadOptions.PreserveWhitespace);
            var cells = sheet.Descendants(Main + "c").ToList();
            Assert.Equal(15, cells.Count);
            Assert.Contains(cells, c => (string?)c.Attribute("r") == "C3" && (string?)c.Attribute("s") == "1");
            Assert.Equal("A1:C5", (string?)sheet.Descendants(Main + "dimension").Single().Attribute("ref"));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task KeepsEmptyCellsThatInheritAVisibleColumnFormat()
    {
        // Column B is yellow. An explicit unformatted B cell is what makes a cell white again, so it must not vanish.
        var path = DocumentFixtures.NewPath("xlsx");
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<worksheet xmlns=\"{main}\">\n<dimension ref=\"A1:B600\"/><cols><col min=\"2\" max=\"2\" width=\"12\" style=\"1\" customWidth=\"1\"/></cols><sheetData>\n");
        sheet.Append("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>\n");
        for (var row = 2; row <= 600; row++)
        {
            sheet.Append($"<row r=\"{row}\"><c r=\"A{row}\" s=\"0\"/><c r=\"B{row}\" s=\"0\"/></row>\n");
        }

        sheet.Append("</sheetData></worksheet>");
        DocumentFixtures.Zip(path, new (string, byte[])[]
        {
            ("[Content_Types].xml", DocumentFixtures.Text(DocumentFixtures.ContentTypes(false, false,
                "/xl/workbook.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                "/xl/worksheets/sheet1.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                "/xl/styles.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))),
            ("_rels/.rels", DocumentFixtures.Text(DocumentFixtures.Rels("rId1|http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument|xl/workbook.xml"))),
            ("xl/workbook.xml", DocumentFixtures.Text($"<?xml version=\"1.0\"?>\n<workbook xmlns=\"{main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
            ("xl/_rels/workbook.xml.rels", DocumentFixtures.Text(DocumentFixtures.Rels(
                "rId1|http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet|worksheets/sheet1.xml",
                "rId2|http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles|styles.xml"))),
            ("xl/styles.xml", DocumentFixtures.Text(DocumentFixtures.Styles)),
            ("xl/worksheets/sheet1.xml", DocumentFixtures.Text(sheet.ToString()))
        });
        try
        {
            await RunAsync(path, ReplaceInPlace);

            var cells = XDocument.Parse(DocumentFixtures.ReadText(path.Value, "xl/worksheets/sheet1.xml"), LoadOptions.PreserveWhitespace).Descendants(Main + "c").ToList();
            Assert.Equal(1 + 599, cells.Count); // every B kept, every plain A dropped
            Assert.All(cells.Skip(1), c => Assert.StartsWith("B", (string?)c.Attribute("r")));
        }
        finally
        {
            TestImages.TryDelete(path);
        }
    }

    // ---- Word / PowerPoint --------------------------------------------------------------------------------

    [Fact]
    public async Task ReEncodesAnEmbeddedJpegWithoutChangingNamesOrReferences()
    {
        var pixels = TestImages.PhotoLikeRgba(1000, 750);
        var photo = TestImages.SaveJpeg(pixels, 1000, 750, quality: 98);
        var body = DocumentFixtures.DocumentWithPictureXml(string.Concat(Enumerable.Range(0, 300).Select(i => $"    <w:p>\n      <w:r>\n        <w:t xml:space=\"preserve\">Line {i} </w:t>\n      </w:r>\n    </w:p>\n")));
        var source = DocumentFixtures.Docx(File.ReadAllBytes(photo.Value), "image1.jpeg", "image/jpeg", body);
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.True(new FileInfo(source.Value).Length < originalSize * 0.8, result.Message);
            DocumentFixtures.AssertValidOpcPackage(source.Value);
            Assert.True(DocumentFixtures.HasEntry(source.Value, "word/media/image1.jpeg"));
            Assert.Contains("media/image1.jpeg", DocumentFixtures.ReadText(source.Value, "word/_rels/document.xml.rels"));
            var jpeg = DocumentFixtures.ReadEntry(source.Value, "word/media/image1.jpeg");
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);

            // Text is untouched, trailing spaces inside runs included.
            var document = XDocument.Parse(DocumentFixtures.ReadText(source.Value, "word/document.xml"), LoadOptions.PreserveWhitespace);
            var texts = document.Descendants(W + "t").Select(t => t.Value).ToList();
            Assert.Equal(300, texts.Count);
            Assert.Equal("Line 7 ", texts[7]);
        }
        finally
        {
            TestImages.TryDelete(source, photo);
        }
    }

    [Fact]
    public async Task PreservesSpaceOnlyRunsInDrawingMl()
    {
        var path = DocumentFixtures.NewPath("pptx");
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var filler = string.Concat(Enumerable.Range(0, 400).Select(i => $"\n      <a:r>\n        <a:t>word{i}</a:t>\n      </a:r>\n      <a:r><a:t> </a:t></a:r>"));
        var slide = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:sld xmlns:a=\"{a}\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">\n  <p:cSld>\n    <a:p>{filler}\n    </a:p>\n  </p:cSld>\n</p:sld>";
        DocumentFixtures.Zip(path, new (string, byte[])[]
        {
            ("[Content_Types].xml", DocumentFixtures.Text(DocumentFixtures.ContentTypes(false, false,
                "/ppt/presentation.xml|application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                "/ppt/slides/slide1.xml|application/vnd.openxmlformats-officedocument.presentationml.slide+xml"))),
            ("_rels/.rels", DocumentFixtures.Text(DocumentFixtures.Rels("rId1|http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument|ppt/presentation.xml"))),
            ("ppt/presentation.xml", DocumentFixtures.Text("<?xml version=\"1.0\"?>\n<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>")),
            ("ppt/slides/slide1.xml", DocumentFixtures.Text(slide))
        });
        try
        {
            var result = await RunAsync(path, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var after = XDocument.Parse(DocumentFixtures.ReadText(path.Value, "ppt/slides/slide1.xml"), LoadOptions.PreserveWhitespace);
            var texts = after.Descendants(XNamespace.Get(a) + "t").Select(t => t.Value).ToList();
            Assert.Equal(800, texts.Count);
            Assert.Equal(" ", texts[1]);
            Assert.DoesNotContain("\n      <a:r>", DocumentFixtures.ReadText(path.Value, "ppt/slides/slide1.xml"));
        }
        finally
        {
            TestImages.TryDelete(path);
        }
    }

    [Fact]
    public async Task AggressiveModeDownscalesAndConvertsPhotographicPngToJpeg()
    {
        var pixels = TestImages.PhotoLikeRgba(2400, 1800);
        var png = TestImages.SavePng(pixels, 2400, 1800);
        var source = DocumentFixtures.Pptx(File.ReadAllBytes(png.Value), "image1.png");
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            var result = await RunAsync(source, Aggressive);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.True(new FileInfo(source.Value).Length < originalSize * 0.25, result.Message);
            DocumentFixtures.AssertValidOpcPackage(source.Value);

            Assert.False(DocumentFixtures.HasEntry(source.Value, "ppt/media/image1.png"));
            Assert.True(DocumentFixtures.HasEntry(source.Value, "ppt/media/image1.jpg"));
            Assert.Contains("../media/image1.jpg", DocumentFixtures.ReadText(source.Value, "ppt/slides/_rels/slide1.xml.rels"));
            Assert.Contains("Extension=\"jpg\"", DocumentFixtures.ReadText(source.Value, "[Content_Types].xml"));

            var output = FilePath.TempFile("squish-docimg-out", "jpg", addUniqueSuffix: true);
            File.WriteAllBytes(output.Value, DocumentFixtures.ReadEntry(source.Value, "ppt/media/image1.jpg"));
            var (width, height) = TestImages.Dimensions(output.Value);
            TestImages.TryDelete(output);
            Assert.InRange(Math.Max(width, height), 1100, 1300); // 8 inches at 150 dpi
        }
        finally
        {
            TestImages.TryDelete(source, png);
        }
    }

    [Fact]
    public async Task AggressiveModeLeavesPicturesInsideGroupsAtFullSize()
    {
        var pixels = TestImages.PhotoLikeRgba(1600, 1200);
        var png = TestImages.SavePng(pixels, 1600, 1200);
        var source = DocumentFixtures.Pptx(File.ReadAllBytes(png.Value), "image1.png", grouped: true);
        try
        {
            await RunAsync(source, Aggressive);

            // The group transform may scale the picture, so its display size is unknown: no resampling, so same dimensions.
            var name = DocumentFixtures.EntryNames(source.Value).Single(n => n.StartsWith("ppt/media/", StringComparison.Ordinal));
            var extracted = FilePath.TempFile("squish-docimg-out", name[(name.LastIndexOf('.') + 1)..], addUniqueSuffix: true);
            File.WriteAllBytes(extracted.Value, DocumentFixtures.ReadEntry(source.Value, name));
            var dimensions = TestImages.Dimensions(extracted.Value);
            TestImages.TryDelete(extracted);
            Assert.Equal((1600, 1200), dimensions);
        }
        finally
        {
            TestImages.TryDelete(source, png);
        }
    }

    // ---- OpenDocument -------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenDocumentKeepsMimetypeFirstAndStoredAndDropsThumbnail()
    {
        var pixels = TestImages.PhotoLikeRgba(1000, 750, seed: 3);
        var photo = TestImages.SaveJpeg(pixels, 1000, 750, quality: 98);
        var source = DocumentFixtures.Odt(File.ReadAllBytes(photo.Value));
        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            using (var zip = ZipFile.OpenRead(source.Value))
            {
                var first = zip.Entries[0];
                Assert.Equal("mimetype", first.FullName);
                Assert.Equal(first.Length, first.CompressedLength);
            }

            Assert.False(DocumentFixtures.HasEntry(source.Value, "Thumbnails/thumbnail.png"));
            Assert.DoesNotContain("Thumbnails", DocumentFixtures.ReadText(source.Value, "META-INF/manifest.xml"));
            Assert.Contains("Pictures/photo.jpg", DocumentFixtures.ReadText(source.Value, "META-INF/manifest.xml"));
            var meta = DocumentFixtures.ReadText(source.Value, "meta.xml");
            Assert.DoesNotContain("Secret", meta);
            Assert.Contains("Keep me", meta);
            Assert.Contains("Hello", DocumentFixtures.ReadText(source.Value, "content.xml"));
        }
        finally
        {
            TestImages.TryDelete(source, photo);
        }
    }

    // ---- things that must be left alone -------------------------------------------------------------------

    [Fact]
    public async Task LeavesOle2AndEncryptedOfficeFilesUntouched()
    {
        var path = DocumentFixtures.NewPath("docx");
        var bytes = new byte[8192];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        File.WriteAllBytes(path.Value, bytes);
        try
        {
            var result = await RunAsync(path, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.Equal(path.Value, result.OutputPath!.Value.Value);
            Assert.Contains("left untouched", result.Message);
            Assert.Equal(bytes, File.ReadAllBytes(path.Value));
        }
        finally
        {
            TestImages.TryDelete(path);
        }
    }

    [Fact]
    public async Task LeavesSignedDocumentsAndLockFilesUntouched()
    {
        var signed = DocumentFixtures.Xlsx(500, 20);
        using (var zip = ZipFile.Open(signed.Value, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("_xmlsignatures/sig1.xml").Open());
            writer.Write("<Signature/>");
        }

        var lockDirectory = Path.Combine(Path.GetTempPath(), "squish-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lockDirectory);
        var lockFile = new FilePath(Path.Combine(lockDirectory, "~$report.xlsx"));
        File.WriteAllBytes(lockFile.Value, new byte[] { 1, 2, 3, 4, 5, 6 });
        var signedBytes = File.ReadAllBytes(signed.Value);
        try
        {
            var signedResult = await RunAsync(signed, ReplaceInPlace);
            var lockResult = await RunAsync(lockFile, ReplaceInPlace);

            Assert.Contains("left untouched", signedResult.Message);
            Assert.Equal(signedBytes, File.ReadAllBytes(signed.Value));
            Assert.Contains("left untouched", lockResult.Message);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, File.ReadAllBytes(lockFile.Value));
        }
        finally
        {
            TestImages.TryDelete(signed);
            Directory.Delete(lockDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task OnlyTreatsSpreadsheetMlRowsAsRows()
    {
        // A drawing anchor inside a worksheet also has an element called "row"; its text must survive.
        var path = DocumentFixtures.NewPath("xlsx");
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<worksheet xmlns=\"{main}\" xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">\n<dimension ref=\"A1:A500\"/><sheetData>\n");
        sheet.Append("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>\n");
        for (var row = 2; row <= 500; row++)
        {
            sheet.Append($"<row r=\"{row}\"><c r=\"A{row}\" s=\"0\"/></row>\n");
        }

        sheet.Append("</sheetData><extLst><ext><xdr:from><xdr:col>3</xdr:col><xdr:row>40</xdr:row></xdr:from></ext></extLst></worksheet>");
        DocumentFixtures.Zip(path, new (string, byte[])[]
        {
            ("[Content_Types].xml", DocumentFixtures.Text(DocumentFixtures.ContentTypes(false, false,
                "/xl/workbook.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                "/xl/worksheets/sheet1.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                "/xl/styles.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))),
            ("_rels/.rels", DocumentFixtures.Text(DocumentFixtures.Rels("rId1|http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument|xl/workbook.xml"))),
            ("xl/workbook.xml", DocumentFixtures.Text($"<?xml version=\"1.0\"?>\n<workbook xmlns=\"{main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
            ("xl/_rels/workbook.xml.rels", DocumentFixtures.Text(DocumentFixtures.Rels(
                "rId1|http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet|worksheets/sheet1.xml",
                "rId2|http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles|styles.xml"))),
            ("xl/styles.xml", DocumentFixtures.Text(DocumentFixtures.Styles)),
            ("xl/worksheets/sheet1.xml", DocumentFixtures.Text(sheet.ToString()))
        });
        try
        {
            var result = await RunAsync(path, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var xml = DocumentFixtures.ReadText(path.Value, "xl/worksheets/sheet1.xml");
            Assert.Contains("<xdr:row>40</xdr:row>", xml);
            Assert.Contains("<xdr:col>3</xdr:col>", xml);
            var cells = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Descendants(Main + "c").ToList();
            Assert.Single(cells);
        }
        finally
        {
            TestImages.TryDelete(path);
        }
    }

    [Fact]
    public async Task EmptyXmlPartsAlreadyInTheSourceDoNotBlockTheRewrite()
    {
        // Real OpenDocument files from LibreOffice contain zero-byte .xml parts (e.g. Configurations2/accelerator/current.xml).
        var photo = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1000, 750, seed: 8), 1000, 750, quality: 98);
        var source = DocumentFixtures.Odt(File.ReadAllBytes(photo.Value));
        using (var zip = ZipFile.Open(source.Value, ZipArchiveMode.Update))
        {
            zip.CreateEntry("Configurations2/accelerator/current.xml");
        }

        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.True(DocumentFixtures.HasEntry(source.Value, "Configurations2/accelerator/current.xml"));
            Assert.False(DocumentFixtures.HasEntry(source.Value, "Thumbnails/thumbnail.png"));
        }
        finally
        {
            TestImages.TryDelete(source, photo);
        }
    }

    [Fact]
    public async Task LeavesDocumentsThatAreOpenInAnEditorUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "squish-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = new FilePath(Path.Combine(directory, "Quarterly report.xlsx"));
        File.Copy(DocumentFixtures.Xlsx(800, 40).Value, source.Value);
        File.WriteAllBytes(Path.Combine(directory, "~$arterly report.xlsx"), new byte[] { 1 });
        var before = File.ReadAllBytes(source.Value);
        try
        {
            var result = await RunAsync(source, ReplaceInPlace);

            Assert.Contains("open in an application", result.Message);
            Assert.Equal(before, File.ReadAllBytes(source.Value));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WritesNextToTheOriginalWhenNotReplacingInPlace()
    {
        var source = DocumentFixtures.Xlsx(1000, 50);
        var before = File.ReadAllBytes(source.Value);
        FilePath? output = null;
        try
        {
            var result = await RunAsync(source, null);

            output = result.OutputPath;
            Assert.NotNull(output);
            Assert.EndsWith(".squish.xlsx", output!.Value.Value);
            Assert.Equal(before, File.ReadAllBytes(source.Value));
            DocumentFixtures.AssertValidOpcPackage(output.Value.Value);
        }
        finally
        {
            TestImages.TryDelete(source, output);
        }
    }

    [Fact]
    public void SnifferRecognisesPackageKindsByContent()
    {
        var xlsx = DocumentFixtures.Xlsx(10, 5);
        var odt = DocumentFixtures.Odt(new byte[100]);
        try
        {
            Assert.Equal(DocumentFormat.Ooxml, DocumentSniffer.Sniff(xlsx.Value).Format);
            Assert.Equal(DocumentFormat.OpenDocument, DocumentSniffer.Sniff(odt.Value).Format);
            Assert.True(DocumentSniffer.IsOfficeLockFile(@"C:\docs\~$budget.xlsx"));
            Assert.False(DocumentSniffer.IsOfficeLockFile(@"C:\docs\budget.xlsx"));
        }
        finally
        {
            TestImages.TryDelete(xlsx, odt);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static Task<OptimisationResult> RunAsync(FilePath source, IReadOnlyDictionary<string, object?>? metadata)
    {
        var request = new OptimisationRequest(ItemType.Document, source, metadata: metadata);
        var context = new OptimiserExecutionContext(request.RequestId, _ => { });
        return new DocumentOptimiser(BuiltIn).OptimiseAsync(request, context, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));
    }
}
