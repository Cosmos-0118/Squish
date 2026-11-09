using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Text;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>Builds small but structurally faithful Office / OpenDocument packages for the document optimiser tests.</summary>
internal static class DocumentFixtures
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static FilePath NewPath(string extension) => FilePath.TempFile("squish-test-doc", extension, addUniqueSuffix: true);

    public static void Zip(FilePath path, IEnumerable<(string Name, byte[] Data)> parts, bool storeFirst = false)
    {
        using var stream = new FileStream(path.Value, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var first = true;
        foreach (var (name, data) in parts)
        {
            var entry = zip.CreateEntry(name, storeFirst && first ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            first = false;
            using var entryStream = entry.Open();
            entryStream.Write(data);
        }
    }

    public static byte[] Text(string text) => new UTF8Encoding(false).GetBytes(text);

    public static string ContentTypes(bool jpeg, bool png, params string[] overrides)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\n  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\n  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\n");
        if (jpeg)
        {
            sb.Append("  <Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>\n");
        }

        if (png)
        {
            sb.Append("  <Default Extension=\"png\" ContentType=\"image/png\"/>\n");
        }

        foreach (var item in overrides)
        {
            var split = item.Split('|');
            sb.Append($"  <Override PartName=\"{split[0]}\" ContentType=\"{split[1]}\"/>\n");
        }

        return sb.Append("</Types>").ToString();
    }

    public static string Rels(params string[] relationships)
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"{RelNs}\">\n");
        foreach (var item in relationships)
        {
            var split = item.Split('|');
            sb.Append($"  <Relationship Id=\"{split[0]}\" Type=\"{split[1]}\" Target=\"{split[2]}\"/>\n");
        }

        return sb.Append("</Relationships>").ToString();
    }

    public const string CoreXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n  <dc:title>Quarterly numbers</dc:title>\n  <dc:creator>Jane Secret</dc:creator>\n  <cp:lastModifiedBy>John Secret</cp:lastModifiedBy>\n  <cp:revision>42</cp:revision>\n</cp:coreProperties>";

    public const string AppXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">\n  <Application>Microsoft Excel</Application>\n  <Company>Secret Corp</Company>\n  <Manager>Boss</Manager>\n</Properties>";

    public const string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<styleSheet xmlns=\"" + Main + "\">\n  <fonts count=\"1\"><font><sz val=\"11\"/></font></fonts>\n  <fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFF00\"/></patternFill></fill></fills>\n  <borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>\n  <cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>\n  <cellXfs count=\"3\">\n    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>\n    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>\n    <xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n  </cellXfs>\n</styleSheet>";

    /// <summary>
    /// A workbook with real data in A1:C5, <paramref name="phantomRows"/> x <paramref name="phantomColumns"/> empty plain cells
    /// below it, one yellow blank cell inside the data and another far below it, plus calcChain and a thumbnail.
    /// </summary>
    public static FilePath Xlsx(int phantomRows, int phantomColumns, bool thumbnail = true)
    {
        var path = NewPath("xlsx");
        var sheet = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<worksheet xmlns=\"{Main}\" xmlns:r=\"{DocRel}\">\n  <dimension ref=\"A1:{ColumnName(phantomColumns)}{5 + phantomRows}\"/>\n  <sheetData>\n");
        for (var row = 1; row <= 5; row++)
        {
            sheet.Append($"    <row r=\"{row}\" spans=\"1:3\">\n");
            sheet.Append($"      <c r=\"A{row}\" t=\"s\"><v>{row % 2}</v></c>\n");
            sheet.Append($"      <c r=\"B{row}\"><v>{row * 10}</v></c>\n");
            sheet.Append(row == 3 ? $"      <c r=\"C{row}\" s=\"1\"/>\n" : $"      <c r=\"C{row}\"><f>B{row}*2</f><v>{row * 20}</v></c>\n");
            sheet.Append("    </row>\n");
        }

        for (var row = 6; row < 6 + phantomRows; row++)
        {
            sheet.Append($"    <row r=\"{row}\">");
            for (var column = 1; column <= phantomColumns; column++)
            {
                // The very last row carries one yellow blank: bloat to a tidy-up, formatting to the user.
                var style = row == 5 + phantomRows && column == 1 ? 1 : 0;
                sheet.Append($"<c r=\"{ColumnName(column)}{row}\" s=\"{style}\"/>");
            }

            sheet.Append("</row>\n");
        }

        sheet.Append("  </sheetData>\n</worksheet>");

        var parts = new List<(string, byte[])>
        {
            ("[Content_Types].xml", Text(ContentTypes(thumbnail, false,
                "/xl/workbook.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                "/xl/worksheets/sheet1.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                "/xl/styles.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml",
                "/xl/sharedStrings.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml",
                "/xl/calcChain.xml|application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml",
                "/docProps/core.xml|application/vnd.openxmlformats-package.core-properties+xml",
                "/docProps/app.xml|application/vnd.openxmlformats-officedocument.extended-properties+xml"))),
            ("_rels/.rels", Text(Rels(
                $"rId1|{DocRel}/officeDocument|xl/workbook.xml",
                "rId2|http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties|docProps/core.xml",
                $"rId3|{DocRel}/extended-properties|docProps/app.xml",
                thumbnail ? "rId4|http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail|docProps/thumbnail.jpeg" : "rId4|http://example.com/unused|docProps/app.xml"))),
            ("docProps/core.xml", Text(CoreXml)),
            ("docProps/app.xml", Text(AppXml)),
            ("xl/workbook.xml", Text($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<workbook xmlns=\"{Main}\" xmlns:r=\"{DocRel}\">\n  <sheets>\n    <sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/>\n  </sheets>\n</workbook>")),
            ("xl/_rels/workbook.xml.rels", Text(Rels(
                $"rId1|{DocRel}/worksheet|worksheets/sheet1.xml",
                $"rId2|{DocRel}/styles|styles.xml",
                $"rId3|{DocRel}/sharedStrings|sharedStrings.xml",
                $"rId4|{DocRel}/calcChain|calcChain.xml"))),
            ("xl/styles.xml", Text(Styles)),
            ("xl/sharedStrings.xml", Text($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<sst xmlns=\"{Main}\" count=\"2\" uniqueCount=\"2\">\n  <si><t xml:space=\"preserve\"> </t></si>\n  <si><t>Total</t></si>\n</sst>")),
            ("xl/calcChain.xml", Text($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<calcChain xmlns=\"{Main}\"><c r=\"C1\" i=\"1\"/><c r=\"C2\"/><c r=\"C4\"/><c r=\"C5\"/></calcChain>")),
            ("xl/worksheets/sheet1.xml", Text(sheet.ToString()))
        };

        if (thumbnail)
        {
            parts.Add(("docProps/thumbnail.jpeg", new byte[2048]));
        }

        Zip(path, parts);
        return path;
    }

    public static FilePath Docx(byte[] imageBytes, string imageName, string imageContentType, string documentBody)
    {
        var path = NewPath("docx");
        var extension = imageName[(imageName.LastIndexOf('.') + 1)..];
        var parts = new List<(string, byte[])>
        {
            ("[Content_Types].xml", Text(ContentTypes(extension == "jpeg", extension == "png",
                "/word/document.xml|application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                "/docProps/core.xml|application/vnd.openxmlformats-package.core-properties+xml"))),
            ("_rels/.rels", Text(Rels(
                $"rId1|{DocRel}/officeDocument|word/document.xml",
                "rId2|http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties|docProps/core.xml"))),
            ("docProps/core.xml", Text(CoreXml)),
            ("word/_rels/document.xml.rels", Text(Rels($"rId5|{DocRel}/image|media/{imageName}"))),
            ("word/document.xml", Text(documentBody)),
            ($"word/media/{imageName}", imageBytes)
        };
        Zip(path, parts);
        _ = imageContentType;
        return path;
    }

    public static string DocumentWithPictureXml(string indentedParagraphs)
        => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"" + DocRel + "\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">\n  <w:body>\n"
           + indentedParagraphs
           + "    <w:p>\n      <w:r>\n        <w:drawing>\n          <pic:pic>\n            <pic:blipFill>\n              <a:blip r:embed=\"rId5\"/>\n            </pic:blipFill>\n            <pic:spPr>\n              <a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"5486400\" cy=\"3657600\"/></a:xfrm>\n            </pic:spPr>\n          </pic:pic>\n        </w:drawing>\n      </w:r>\n    </w:p>\n  </w:body>\n</w:document>";

    /// <summary>A one-slide deck whose single picture is drawn 8 x 6 inches (so ~1200 x 900 px is plenty at 150 dpi).</summary>
    public static FilePath Pptx(byte[] imageBytes, string imageName, bool grouped = false)
    {
        var path = NewPath("pptx");
        var extension = imageName[(imageName.LastIndexOf('.') + 1)..];
        var picture = "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Picture\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"7315200\" cy=\"5486400\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";
        var shapes = grouped ? $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"3\" name=\"g\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{picture}</p:grpSp>" : picture;
        var slide = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"" + DocRel + "\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + shapes + "</p:spTree></p:cSld></p:sld>";
        var parts = new List<(string, byte[])>
        {
            ("[Content_Types].xml", Text(ContentTypes(extension == "jpeg", extension == "png",
                "/ppt/presentation.xml|application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                "/ppt/slides/slide1.xml|application/vnd.openxmlformats-officedocument.presentationml.slide+xml"))),
            ("_rels/.rels", Text(Rels($"rId1|{DocRel}/officeDocument|ppt/presentation.xml"))),
            ("ppt/presentation.xml", Text("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"" + DocRel + "\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst></p:presentation>")),
            ("ppt/_rels/presentation.xml.rels", Text(Rels($"rId1|{DocRel}/slide|slides/slide1.xml"))),
            ("ppt/slides/slide1.xml", Text(slide)),
            ("ppt/slides/_rels/slide1.xml.rels", Text(Rels($"rId2|{DocRel}/image|../media/{imageName}"))),
            ($"ppt/media/{imageName}", imageBytes)
        };
        Zip(path, parts);
        return path;
    }

    public static FilePath Odt(byte[] pictureBytes)
    {
        var path = NewPath("odt");
        const string office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        const string manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        var parts = new List<(string, byte[])>
        {
            ("mimetype", Text("application/vnd.oasis.opendocument.text")),
            ("META-INF/manifest.xml", Text($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<manifest:manifest xmlns:manifest=\"{manifest}\" manifest:version=\"1.2\">\n  <manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>\n  <manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>\n  <manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/>\n  <manifest:file-entry manifest:full-path=\"Pictures/photo.jpg\" manifest:media-type=\"image/jpeg\"/>\n  <manifest:file-entry manifest:full-path=\"Thumbnails/thumbnail.png\" manifest:media-type=\"image/png\"/>\n</manifest:manifest>")),
            ("content.xml", Text($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-content xmlns:office=\"{office}\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">\n<office:body><office:text><text:p>Hello <text:span> </text:span> world</text:p><draw:frame><draw:image xlink:href=\"Pictures/photo.jpg\"/></draw:frame></office:text></office:body>\n</office:document-content>")),
            ("meta.xml", Text($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-meta xmlns:office=\"{office}\" xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n  <office:meta>\n    <meta:initial-creator>Jane Secret</meta:initial-creator>\n    <dc:creator>John Secret</dc:creator>\n    <dc:title>Keep me</dc:title>\n  </office:meta>\n</office:document-meta>")),
            ("Pictures/photo.jpg", pictureBytes),
            ("Thumbnails/thumbnail.png", new byte[4096])
        };
        Zip(path, parts, storeFirst: true);
        return path;
    }

    public static string ColumnName(int column)
    {
        var name = string.Empty;
        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }

        return name;
    }

    // ---- verification helpers -------------------------------------------------------------------------

    public static byte[] ReadEntry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"{name} not in {path}");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static string ReadText(string path, string name) => Encoding.UTF8.GetString(ReadEntry(path, name));

    public static bool HasEntry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.GetEntry(name) is not null;
    }

    public static IReadOnlyList<string> EntryNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var names = new List<string>();
        foreach (var entry in zip.Entries)
        {
            names.Add(entry.FullName);
        }

        return names;
    }

    /// <summary>Opens the file with System.IO.Packaging (strict OPC rules) and checks every part and every internal relationship resolves.</summary>
    public static void AssertValidOpcPackage(string path)
    {
        using var package = Package.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var part in package.GetParts())
        {
            if (PackUriHelper.IsRelationshipPartUri(part.Uri))
            {
                continue;
            }

            using var stream = part.GetStream();
            stream.CopyTo(Stream.Null);
            foreach (var relationship in part.GetRelationships())
            {
                if (relationship.TargetMode == TargetMode.Internal)
                {
                    Assert.True(package.PartExists(PackUriHelper.ResolvePartUri(part.Uri, relationship.TargetUri)), $"{part.Uri} -> {relationship.TargetUri}");
                }
            }
        }

        foreach (var relationship in package.GetRelationships())
        {
            if (relationship.TargetMode == TargetMode.Internal)
            {
                Assert.True(package.PartExists(PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), relationship.TargetUri)), $"package -> {relationship.TargetUri}");
            }
        }
    }
}
