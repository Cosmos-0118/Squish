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

/// <summary>Media planning, picture sizing and spreadsheet row clean-up in Office / OpenDocument packages.</summary>
public sealed class DocumentMediaTests
{
    private const string DocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

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

    // ---- duplicates and unused media ------------------------------------------------------------------------

    [Fact]
    public async Task IdenticalPicturesOnEverySlideAreStoredOnceAndEveryRelationshipFollows()
    {
        var logo = TestImages.SavePng(TestImages.GraphicRgba(600, 400), 600, 400);
        var bytes = File.ReadAllBytes(logo.Value);
        var source = Deck(slideCount: 3, mediaNames: ["image1.png", "image2.png", "image3.png"], extraMedia: bytes);
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            // Pictures are left alone on purpose: merging copies is structural and must not depend on re-encoding.
            var result = await RunAsync(source, ReplaceInPlace, BuiltIn with { OptimiseImages = false });

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.True(new FileInfo(source.Value).Length < originalSize * 0.6, result.Message);
            DocumentFixtures.AssertValidOpcPackage(source.Value);

            var media = DocumentFixtures.EntryNames(source.Value).Where(n => n.StartsWith("ppt/media/", StringComparison.Ordinal)).ToList();
            Assert.Equal(["ppt/media/image1.png"], media);
            for (var slide = 1; slide <= 3; slide++)
            {
                Assert.Contains("../media/image1.png", DocumentFixtures.ReadText(source.Value, $"ppt/slides/_rels/slide{slide}.xml.rels"));
            }

            Assert.Equal(bytes, DocumentFixtures.ReadEntry(source.Value, "ppt/media/image1.png"));
            Assert.Contains("merged 2 duplicate media files", result.Message);
        }
        finally
        {
            TestImages.TryDelete(source, logo);
        }
    }

    [Fact]
    public async Task MergedCopiesFollowTheirKeeperWhenItIsConvertedToAnotherFormat()
    {
        var photo = TestImages.SavePng(TestImages.PhotoLikeRgba(1600, 1200, seed: 4), 1600, 1200);
        var bytes = File.ReadAllBytes(photo.Value);
        var source = Deck(slideCount: 2, mediaNames: ["image1.png", "image2.png"], extraMedia: bytes);
        try
        {
            var result = await RunAsync(source, Aggressive, BuiltIn);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            DocumentFixtures.AssertValidOpcPackage(source.Value);
            var media = DocumentFixtures.EntryNames(source.Value).Where(n => n.StartsWith("ppt/media/", StringComparison.Ordinal)).ToList();
            var only = Assert.Single(media);
            var file = only[(only.LastIndexOf('/') + 1)..];
            Assert.Contains($"../media/{file}", DocumentFixtures.ReadText(source.Value, "ppt/slides/_rels/slide1.xml.rels"));
            Assert.Contains($"../media/{file}", DocumentFixtures.ReadText(source.Value, "ppt/slides/_rels/slide2.xml.rels"));
        }
        finally
        {
            TestImages.TryDelete(source, photo);
        }
    }

    [Fact]
    public async Task PicturesNoRelationshipPointsToAreDropped()
    {
        var used = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1000, 750, seed: 2), 1000, 750, quality: 90);
        var orphan = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1000, 750, seed: 9), 1000, 750, quality: 90);
        var body = DocumentFixtures.DocumentWithPictureXml(string.Empty);
        var source = DocumentFixtures.Docx(File.ReadAllBytes(used.Value), "image1.jpeg", "image/jpeg", body);
        AddEntry(source, "word/media/leftover.jpeg", File.ReadAllBytes(orphan.Value));
        var originalSize = new FileInfo(source.Value).Length;
        try
        {
            var result = await RunAsync(source, ReplaceInPlace, BuiltIn with { OptimiseImages = false });

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            Assert.True(new FileInfo(source.Value).Length < originalSize * 0.7, result.Message);
            DocumentFixtures.AssertValidOpcPackage(source.Value);
            Assert.True(DocumentFixtures.HasEntry(source.Value, "word/media/image1.jpeg"));
            Assert.False(DocumentFixtures.HasEntry(source.Value, "word/media/leftover.jpeg"));
            Assert.Contains("removed 1 unused media file", result.Message);
        }
        finally
        {
            TestImages.TryDelete(source, used, orphan);
        }
    }

    // ---- sizing --------------------------------------------------------------------------------------------

    [Fact]
    public async Task NormalModeAlsoShrinksPicturesShownFarAboveTheTargetResolution()
    {
        // 6 x 4 inch picture, 3000 px wide = 500 dpi; the normal target is 220 dpi = 1320 px.
        var photo = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(3000, 2000, seed: 5), 3000, 2000, quality: 95);
        var source = DocumentFixtures.Docx(File.ReadAllBytes(photo.Value), "image1.jpeg", "image/jpeg", DocumentFixtures.DocumentWithPictureXml(string.Empty));
        try
        {
            var result = await RunAsync(source, ReplaceInPlace, BuiltIn);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var (width, _) = DimensionsOf(source.Value, "word/media/image1.jpeg");
            Assert.InRange(width, 1250, 1450);
        }
        finally
        {
            TestImages.TryDelete(source, photo);
        }
    }

    [Fact]
    public async Task NormalModeKeepsPicturesThatAreNotShownMuchLargerThanNeeded()
    {
        // 6 inches at 1500 px = 250 dpi: above 220 but within the tolerance where resampling costs more than it saves.
        var photo = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1500, 1000, seed: 6), 1500, 1000, quality: 95);
        var source = DocumentFixtures.Docx(File.ReadAllBytes(photo.Value), "image1.jpeg", "image/jpeg", DocumentFixtures.DocumentWithPictureXml(string.Empty));
        try
        {
            await RunAsync(source, ReplaceInPlace, BuiltIn);

            Assert.Equal(1500, DimensionsOf(source.Value, "word/media/image1.jpeg").Width);
        }
        finally
        {
            TestImages.TryDelete(source, photo);
        }
    }

    [Fact]
    public async Task PicturesInsideScaledGroupsAreSizedThroughTheGroupTransform()
    {
        // The group maps a 16 x 12 inch child space onto 8 x 6 inches; the picture fills the child space, so it is shown 8 x 6.
        var png = TestImages.SavePng(TestImages.PhotoLikeRgba(2400, 1800, seed: 3), 2400, 1800);
        var picture = "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"P\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"14630400\" cy=\"10972800\"/></a:xfrm></p:spPr></p:pic>";
        var group = $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"g\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"7315200\" cy=\"5486400\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"14630400\" cy=\"10972800\"/></a:xfrm></p:grpSpPr>{picture}</p:grpSp>";
        var source = SingleSlideDeck(group, File.ReadAllBytes(png.Value), "image1.png");
        try
        {
            await RunAsync(source, Aggressive, BuiltIn);

            var name = DocumentFixtures.EntryNames(source.Value).Single(n => n.StartsWith("ppt/media/", StringComparison.Ordinal));
            Assert.InRange(DimensionsOf(source.Value, name).Width, 1100, 1300); // 8 inches at 150 dpi
        }
        finally
        {
            TestImages.TryDelete(source, png);
        }
    }

    [Fact]
    public async Task ShapePictureFillAndSlideBackgroundAreSizedFromTheirBox()
    {
        var shapeImage = TestImages.SavePng(TestImages.PhotoLikeRgba(2400, 1800, seed: 12), 2400, 1800);
        var shape = "<p:sp><p:nvSpPr><p:cNvPr id=\"5\" name=\"Rounded\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"7315200\" cy=\"5486400\"/></a:xfrm><a:prstGeom prst=\"roundRect\"><a:avLst/></a:prstGeom><a:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill></p:spPr></p:sp>";
        var shapeDeck = SingleSlideDeck(shape, File.ReadAllBytes(shapeImage.Value), "image1.png");

        // 13.33 x 7.5 inch slide: 150 dpi is 2000 px across.
        var backgroundImage = TestImages.SavePng(TestImages.PhotoLikeRgba(3200, 1800, seed: 13), 3200, 1800);
        var background = "<p:bg><p:bgPr><a:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill><a:effectLst/></p:bgPr></p:bg>";
        var backgroundDeck = SingleSlideDeck(string.Empty, File.ReadAllBytes(backgroundImage.Value), "image1.png", background: background);
        try
        {
            await RunAsync(shapeDeck, Aggressive, BuiltIn);
            await RunAsync(backgroundDeck, Aggressive, BuiltIn);

            var shapeMedia = DocumentFixtures.EntryNames(shapeDeck.Value).Single(n => n.StartsWith("ppt/media/", StringComparison.Ordinal));
            Assert.InRange(DimensionsOf(shapeDeck.Value, shapeMedia).Width, 1100, 1300);
            var backgroundMedia = DocumentFixtures.EntryNames(backgroundDeck.Value).Single(n => n.StartsWith("ppt/media/", StringComparison.Ordinal));
            Assert.InRange(DimensionsOf(backgroundDeck.Value, backgroundMedia).Width, 1900, 2100);
        }
        finally
        {
            TestImages.TryDelete(shapeDeck, backgroundDeck, shapeImage, backgroundImage);
        }
    }

    [Fact]
    public async Task TiledPictureFillKeepsFullResolution()
    {
        var png = TestImages.SavePng(TestImages.PhotoLikeRgba(1600, 1200, seed: 14), 1600, 1200);
        var shape = "<p:sp><p:nvSpPr><p:cNvPr id=\"5\" name=\"Tiled\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"7315200\" cy=\"5486400\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:blipFill><a:blip r:embed=\"rId2\"/><a:tile tx=\"0\" ty=\"0\" sx=\"100000\" sy=\"100000\" flip=\"none\" algn=\"tl\"/></a:blipFill></p:spPr></p:sp>";
        var source = SingleSlideDeck(shape, File.ReadAllBytes(png.Value), "image1.png");
        try
        {
            await RunAsync(source, Aggressive, BuiltIn);

            var name = DocumentFixtures.EntryNames(source.Value).Single(n => n.StartsWith("ppt/media/", StringComparison.Ordinal));
            Assert.Equal(1600, DimensionsOf(source.Value, name).Width);
        }
        finally
        {
            TestImages.TryDelete(source, png);
        }
    }

    // ---- spreadsheets ---------------------------------------------------------------------------------------

    [Fact]
    public async Task EmptyRowsLeftByWholeSheetFormattingAreDroppedButMeaningfulOnesStay()
    {
        var rows = new StringBuilder();
        rows.Append("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>");
        // Rows that only restate defaults: style 0 and the sheet's own height.
        for (var row = 2; row <= 400; row++)
        {
            rows.Append(row % 2 == 0 ? $"<row r=\"{row}\" s=\"0\" customFormat=\"1\" ht=\"15\" customHeight=\"1\"/>" : $"<row r=\"{row}\" ht=\"15\" customHeight=\"1\"/>");
        }

        rows.Append("<row r=\"401\" hidden=\"1\"/>");
        rows.Append("<row r=\"402\" ht=\"42\" customHeight=\"1\"/>");
        rows.Append("<row r=\"403\" s=\"1\" customFormat=\"1\"/>");
        // A row-level number format draws nothing now but decides what is typed there later: kept in normal mode.
        rows.Append("<row r=\"404\" s=\"2\" customFormat=\"1\"/>");
        var source = XlsxWithSheet(rows.ToString(), dimension: "A1:C404");
        try
        {
            var result = await RunAsync(source, ReplaceInPlace, BuiltIn);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            var sheet = XDocument.Parse(DocumentFixtures.ReadText(source.Value, "xl/worksheets/sheet1.xml"));
            var kept = sheet.Descendants(XName.Get("row", Main)).Select(r => (int)r.Attribute("r")!).ToList();
            Assert.Equal([1, 401, 402, 403, 404], kept);
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task AggressiveModeDropsCustomHeightsAndFillsOfRowsBelowAllData()
    {
        var rows = new StringBuilder();
        rows.Append("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>");
        rows.Append("<row r=\"2\" ht=\"42\" customHeight=\"1\"/>");
        rows.Append("<row r=\"3\" s=\"1\" customFormat=\"1\"/>");
        rows.Append("<row r=\"4\" outlineLevel=\"1\"/>");
        rows.Append("<row r=\"5\" s=\"2\" customFormat=\"1\"/>");
        var normal = XlsxWithSheet(rows.ToString(), dimension: "A1:C5");
        var aggressive = XlsxWithSheet(rows.ToString(), dimension: "A1:C5");
        try
        {
            await RunAsync(normal, ReplaceInPlace, BuiltIn with { MinimumSavingsPercent = 0 });
            await RunAsync(aggressive, Aggressive, BuiltIn with { MinimumSavingsPercent = 0 });

            static List<int> Rows(string path) => XDocument.Parse(DocumentFixtures.ReadText(path, "xl/worksheets/sheet1.xml"))
                .Descendants(XName.Get("row", Main)).Select(r => (int)r.Attribute("r")!).ToList();

            Assert.Equal([1, 2, 3, 4, 5], Rows(normal.Value));
            Assert.Equal([1, 4], Rows(aggressive.Value));
        }
        finally
        {
            TestImages.TryDelete(normal, aggressive);
        }
    }

    [Fact]
    public async Task AggressiveModeKeepsTheFormattingOfASheetWithNoDataAtAll()
    {
        // A printable form: borders and fills on blank cells, nothing typed yet. Without data nothing is "below the data".
        var rows = new StringBuilder();
        for (var row = 1; row <= 30; row++)
        {
            rows.Append($"<row r=\"{row}\"><c r=\"A{row}\" s=\"1\"/><c r=\"B{row}\" s=\"1\"/><c r=\"C{row}\" s=\"0\"/></row>");
        }

        var source = XlsxWithSheet(rows.ToString(), dimension: "A1:C30");
        try
        {
            await RunAsync(source, Aggressive, BuiltIn with { MinimumSavingsPercent = 0 });

            var sheet = XDocument.Parse(DocumentFixtures.ReadText(source.Value, "xl/worksheets/sheet1.xml"));
            var cells = sheet.Descendants(XName.Get("c", Main)).Select(c => (string)c.Attribute("r")!).ToList();
            Assert.Equal(60, cells.Count); // every yellow blank kept; the unformatted ones in column C are the only bloat
            Assert.DoesNotContain(cells, c => c.StartsWith('C'));
            Assert.Equal("A1:B30", (string?)sheet.Descendants(XName.Get("dimension", Main)).Single().Attribute("ref"));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    [Fact]
    public async Task DimensionIsUntouchedWhenNothingWasDropped()
    {
        var rows = "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row><row r=\"2\"><c r=\"B2\"><v>2</v></c></row>";
        var source = XlsxWithSheet(rows, dimension: "A1:Z99");
        try
        {
            await RunAsync(source, ReplaceInPlace, BuiltIn with { MinimumSavingsPercent = 0 });

            var sheet = XDocument.Parse(DocumentFixtures.ReadText(source.Value, "xl/worksheets/sheet1.xml"));
            Assert.Equal("A1:Z99", (string?)sheet.Descendants(XName.Get("dimension", Main)).Single().Attribute("ref"));
        }
        finally
        {
            TestImages.TryDelete(source);
        }
    }

    // ---- OpenDocument -------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenDocumentWithMimetypeInTheWrongPlaceIsRepackedWithItFirst()
    {
        var photo = TestImages.SaveJpeg(TestImages.PhotoLikeRgba(1000, 750, seed: 3), 1000, 750, quality: 98);
        var source = DocumentFixtures.Odt(File.ReadAllBytes(photo.Value));
        var parts = DocumentFixtures.EntryNames(source.Value).Where(n => n != "mimetype").Select(n => (n, DocumentFixtures.ReadEntry(source.Value, n))).ToList();
        parts.Add(("mimetype", DocumentFixtures.ReadEntry(source.Value, "mimetype")));
        DocumentFixtures.Zip(source, parts);
        try
        {
            var result = await RunAsync(source, ReplaceInPlace, BuiltIn);

            Assert.Equal(OptimisationStatus.Succeeded, result.Status);
            using var zip = ZipFile.OpenRead(source.Value);
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
        }
        finally
        {
            TestImages.TryDelete(source, photo);
        }
    }

    // ---- fixtures ------------------------------------------------------------------------------------------

    private static (int Width, int Height) DimensionsOf(string package, string entry)
    {
        var extracted = FilePath.TempFile("squish-docimg-out", entry[(entry.LastIndexOf('.') + 1)..], addUniqueSuffix: true);
        try
        {
            File.WriteAllBytes(extracted.Value, DocumentFixtures.ReadEntry(package, entry));
            return TestImages.Dimensions(extracted.Value);
        }
        finally
        {
            TestImages.TryDelete(extracted);
        }
    }

    private static void AddEntry(FilePath package, string name, byte[] data)
    {
        using var zip = ZipFile.Open(package.Value, ZipArchiveMode.Update);
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }

    /// <summary>A deck of <paramref name="slideCount"/> slides, each with its own relationship to its own copy of a picture.</summary>
    private static FilePath Deck(int slideCount, string[] mediaNames, byte[] extraMedia)
    {
        var path = DocumentFixtures.NewPath("pptx");
        var overrides = new List<string> { "/ppt/presentation.xml|application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml" };
        var parts = new List<(string, byte[])>();
        var slideIds = new StringBuilder();
        var presentationRels = new List<string>();
        for (var slide = 1; slide <= slideCount; slide++)
        {
            overrides.Add($"/ppt/slides/slide{slide}.xml|application/vnd.openxmlformats-officedocument.presentationml.slide+xml");
            slideIds.Append($"<p:sldId id=\"{255 + slide}\" r:id=\"rId{slide}\"/>");
            presentationRels.Add($"rId{slide}|{DocRel}/slide|slides/slide{slide}.xml");
            var picture = $"<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Logo\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId2\"/></p:blipFill><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"609600\"/></a:xfrm></p:spPr></p:pic>";
            parts.Add(($"ppt/slides/slide{slide}.xml", DocumentFixtures.Text($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:sld xmlns:a=\"{A}\" xmlns:r=\"{DocRel}\" xmlns:p=\"{P}\"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{picture}</p:spTree></p:cSld></p:sld>")));
            parts.Add(($"ppt/slides/_rels/slide{slide}.xml.rels", DocumentFixtures.Text(DocumentFixtures.Rels($"rId2|{DocRel}/image|../media/{mediaNames[slide - 1]}"))));
            parts.Add(($"ppt/media/{mediaNames[slide - 1]}", extraMedia));
        }

        parts.Insert(0, ("[Content_Types].xml", DocumentFixtures.Text(DocumentFixtures.ContentTypes(false, true, overrides.ToArray()))));
        parts.Insert(1, ("_rels/.rels", DocumentFixtures.Text(DocumentFixtures.Rels($"rId1|{DocRel}/officeDocument|ppt/presentation.xml"))));
        parts.Insert(2, ("ppt/presentation.xml", DocumentFixtures.Text($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{DocRel}\"><p:sldIdLst>{slideIds}</p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/></p:presentation>")));
        parts.Insert(3, ("ppt/_rels/presentation.xml.rels", DocumentFixtures.Text(DocumentFixtures.Rels(presentationRels.ToArray()))));
        DocumentFixtures.Zip(path, parts);
        return path;
    }

    private static FilePath SingleSlideDeck(string shapes, byte[] image, string imageName, string background = "")
    {
        var path = DocumentFixtures.NewPath("pptx");
        var slide = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:sld xmlns:a=\"{A}\" xmlns:r=\"{DocRel}\" xmlns:p=\"{P}\"><p:cSld>{background}<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{shapes}</p:spTree></p:cSld></p:sld>";
        DocumentFixtures.Zip(path,
        [
            ("[Content_Types].xml", DocumentFixtures.Text(DocumentFixtures.ContentTypes(false, true,
                "/ppt/presentation.xml|application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                "/ppt/slides/slide1.xml|application/vnd.openxmlformats-officedocument.presentationml.slide+xml"))),
            ("_rels/.rels", DocumentFixtures.Text(DocumentFixtures.Rels($"rId1|{DocRel}/officeDocument|ppt/presentation.xml"))),
            ("ppt/presentation.xml", DocumentFixtures.Text($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{DocRel}\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/></p:presentation>")),
            ("ppt/_rels/presentation.xml.rels", DocumentFixtures.Text(DocumentFixtures.Rels($"rId1|{DocRel}/slide|slides/slide1.xml"))),
            ("ppt/slides/slide1.xml", DocumentFixtures.Text(slide)),
            ("ppt/slides/_rels/slide1.xml.rels", DocumentFixtures.Text(DocumentFixtures.Rels($"rId2|{DocRel}/image|../media/{imageName}"))),
            ($"ppt/media/{imageName}", image)
        ]);
        return path;
    }

    private static FilePath XlsxWithSheet(string rows, string dimension)
    {
        var path = DocumentFixtures.NewPath("xlsx");
        var sheet = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<worksheet xmlns=\"{Main}\" xmlns:r=\"{DocRel}\">\n  <dimension ref=\"{dimension}\"/>\n  <sheetFormatPr defaultRowHeight=\"15\"/>\n  <sheetData>{rows}</sheetData>\n</worksheet>";
        DocumentFixtures.Zip(path,
        [
            ("[Content_Types].xml", DocumentFixtures.Text(DocumentFixtures.ContentTypes(false, false,
                "/xl/workbook.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                "/xl/worksheets/sheet1.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                "/xl/styles.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))),
            ("_rels/.rels", DocumentFixtures.Text(DocumentFixtures.Rels($"rId1|{DocRel}/officeDocument|xl/workbook.xml"))),
            ("xl/workbook.xml", DocumentFixtures.Text($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<workbook xmlns=\"{Main}\" xmlns:r=\"{DocRel}\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
            ("xl/_rels/workbook.xml.rels", DocumentFixtures.Text(DocumentFixtures.Rels($"rId1|{DocRel}/worksheet|worksheets/sheet1.xml", $"rId2|{DocRel}/styles|styles.xml"))),
            ("xl/styles.xml", DocumentFixtures.Text(DocumentFixtures.Styles)),
            ("xl/worksheets/sheet1.xml", DocumentFixtures.Text(sheet))
        ]);
        return path;
    }

    private static Task<OptimisationResult> RunAsync(FilePath source, IReadOnlyDictionary<string, object?>? metadata, DocumentOptimiserOptions options)
    {
        var request = new OptimisationRequest(ItemType.Document, source, metadata: metadata);
        var context = new OptimiserExecutionContext(request.RequestId, _ => { });
        return new DocumentOptimiser(options).OptimiseAsync(request, context, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));
    }
}
