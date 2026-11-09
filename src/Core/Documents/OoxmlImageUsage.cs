using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace Squish.Core.Documents;

/// <summary>The largest area (in 1/914400 inch units) any placement of an image occupies; null width means "unknown, do not downscale".</summary>
internal readonly record struct ImageDisplayNeed(double WidthInches, double HeightInches);

/// <summary>
/// Works out how large each embedded picture is actually shown, from the drawing parts that reference it. An image is only
/// reported as downscalable when every reference is understood: a picture, a stretched picture fill of a shape or a slide
/// background, with a known size - optionally inside groups whose scale is known. Anything else (tiled fills, rotated groups,
/// diagram drawings, VML, bullets) leaves it at full resolution.
/// </summary>
internal static class OoxmlImageUsage
{
    private const double EmuPerInch = 914400d;

    private const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PresentationNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string SpreadsheetDrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private const string PictureNs = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string WordShapeNs = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

    private sealed class Placement
    {
        public int OtherBlips;
        public bool Unknown;
        public double WidthInches;
        public double HeightInches;
        public int Pictures;
    }

    /// <summary>Maps media entry name to the size it needs, for entries whose every use is understood.</summary>
    public static Dictionary<string, ImageDisplayNeed> Analyse(ZipArchive archive, IReadOnlySet<string> mediaEntries)
    {
        var accumulated = new Dictionary<string, Placement>(StringComparer.Ordinal);
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        var slideSize = ReadSlideSize(archive);

        foreach (var entry in archive.Entries)
        {
            if (!PackagePaths.IsRelationshipsPart(entry.FullName))
            {
                continue;
            }

            List<RelationshipReader.Relationship> relationships;
            try
            {
                using var stream = entry.Open();
                relationships = RelationshipReader.Read(stream, entry.FullName);
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
            {
                continue;
            }

            var mediaRelationships = relationships.FindAll(r => mediaEntries.Contains(r.ResolvedTarget));
            if (mediaRelationships.Count == 0)
            {
                continue;
            }

            var source = PackagePaths.RelationshipSource(entry.FullName);
            var sourceEntry = source.Length > 0 && source.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? archive.GetEntry(source) : null;
            Dictionary<string, Placement>? byRelationshipId = null;
            if (sourceEntry is not null)
            {
                try
                {
                    byRelationshipId = ScanDrawingPart(sourceEntry, IsSlidePart(source) ? slideSize : null);
                }
                catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
                {
                    byRelationshipId = null;
                }
            }

            foreach (var relationship in mediaRelationships)
            {
                if (byRelationshipId is null || !byRelationshipId.TryGetValue(relationship.Id, out var placement) || placement.Unknown || placement.OtherBlips > 0 || placement.Pictures == 0)
                {
                    unknown.Add(relationship.ResolvedTarget);
                    continue;
                }

                if (!accumulated.TryGetValue(relationship.ResolvedTarget, out var total))
                {
                    total = new Placement();
                    accumulated[relationship.ResolvedTarget] = total;
                }

                total.WidthInches = Math.Max(total.WidthInches, placement.WidthInches);
                total.HeightInches = Math.Max(total.HeightInches, placement.HeightInches);
                total.Pictures += placement.Pictures;
            }
        }

        var result = new Dictionary<string, ImageDisplayNeed>(StringComparer.Ordinal);
        foreach (var (name, placement) in accumulated)
        {
            if (!unknown.Contains(name) && placement.WidthInches > 0 && placement.HeightInches > 0)
            {
                result[name] = new ImageDisplayNeed(placement.WidthInches, placement.HeightInches);
            }
        }

        return result;
    }

    /// <summary>Slide, layout and master backgrounds fill the presentation's slide size; notes and handouts have their own.</summary>
    private static bool IsSlidePart(string partName)
        => partName.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase)
           || partName.StartsWith("ppt/slideLayouts/", StringComparison.OrdinalIgnoreCase)
           || partName.StartsWith("ppt/slideMasters/", StringComparison.OrdinalIgnoreCase);

    private static (double Cx, double Cy)? ReadSlideSize(ZipArchive archive)
    {
        if (archive.GetEntry("ppt/presentation.xml") is not { } entry)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sldSz" && reader.NamespaceURI == PresentationNs)
                {
                    var cx = ParseDouble(reader.GetAttribute("cx"));
                    var cy = ParseDouble(reader.GetAttribute("cy"));
                    return cx > 0 && cy > 0 ? (cx, cy) : null;
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            // Without a slide size backgrounds simply stay at full resolution.
        }

        return null;
    }

    private enum FrameKind
    {
        Picture,
        Shape,
        Background
    }

    /// <summary>A picture, a shape with a picture fill, or a background: one place a picture is shown.</summary>
    private sealed class Frame
    {
        public FrameKind Kind;
        public int Depth;
        public string Element = string.Empty;
        public readonly List<string> Embeds = new();
        public double Cx;
        public double Cy;
        public bool HasExtent;
        public bool Rotated;
        public double CropX = 1;
        public double CropY = 1;
        public double ScaleX = 1;
        public double ScaleY = 1;
        public bool InGroup;
        public bool Unknown;
    }

    /// <summary>The coordinate mapping a group applies to its children: extent over child extent.</summary>
    private sealed class Group
    {
        public int Depth;
        public double ExtCx;
        public double ExtCy;
        public double ChildCx;
        public double ChildCy;
        public bool Rotated;

        public bool Known => ExtCx > 0 && ExtCy > 0 && ChildCx > 0 && ChildCy > 0 && !Rotated;
    }

    private static Dictionary<string, Placement> ScanDrawingPart(ZipArchiveEntry entry, (double Cx, double Cy)? backgroundSize)
    {
        var placements = new Dictionary<string, Placement>(StringComparer.Ordinal);
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false });

        var path = new List<string>();
        var groups = new Stack<Group>();
        Frame? frame = null;

        Placement Get(string id)
        {
            if (!placements.TryGetValue(id, out var p))
            {
                p = new Placement();
                placements[id] = p;
            }

            return p;
        }

        string Parent(int up) => path.Count >= up ? path[^up] : string.Empty;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (path.Count > 0)
                {
                    path.RemoveAt(path.Count - 1);
                }

                if (frame is not null && reader.Depth == frame.Depth && reader.LocalName == frame.Element)
                {
                    Commit(frame, Get);
                    frame = null;
                }
                else if (frame is null && groups.Count > 0 && reader.Depth == groups.Peek().Depth && IsGroup(reader.LocalName))
                {
                    groups.Pop();
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var name = reader.LocalName;
            var depth = reader.Depth;
            var empty = reader.IsEmptyElement;

            switch (name)
            {
                case "grpSp" or "wgp" when frame is null && IsGroup(name):
                    if (!empty)
                    {
                        groups.Push(new Group { Depth = depth });
                    }

                    break;

                case "pic" when frame is null && IsPicture(reader.NamespaceURI):
                    frame = StartFrame(FrameKind.Picture, name, depth, groups);
                    break;

                case "sp" or "wsp" when frame is null && IsShape(reader.NamespaceURI):
                    frame = StartFrame(FrameKind.Shape, name, depth, groups);
                    break;

                case "bg" when frame is null && reader.NamespaceURI == PresentationNs && backgroundSize is { } size:
                    frame = new Frame { Kind = FrameKind.Background, Depth = depth, Element = name, Cx = size.Cx, Cy = size.Cy, HasExtent = true };
                    break;

                case "xfrm" when frame is null && groups.Count > 0 && Parent(1) == "grpSpPr" && groups.Peek().Depth == depth - 2:
                    // 60000ths of a degree; only a half turn leaves the child axes where they were.
                    groups.Peek().Rotated = Math.Abs(ParseDouble(reader.GetAttribute("rot")) / 60000d) % 180d > 0.01;
                    break;

                case "ext" when frame is null && groups.Count > 0 && Parent(1) == "xfrm" && Parent(2) == "grpSpPr" && groups.Peek().Depth == depth - 3:
                    groups.Peek().ExtCx = ParseDouble(reader.GetAttribute("cx"));
                    groups.Peek().ExtCy = ParseDouble(reader.GetAttribute("cy"));
                    break;

                case "chExt" when frame is null && groups.Count > 0 && Parent(1) == "xfrm" && Parent(2) == "grpSpPr" && groups.Peek().Depth == depth - 3:
                    groups.Peek().ChildCx = ParseDouble(reader.GetAttribute("cx"));
                    groups.Peek().ChildCy = ParseDouble(reader.GetAttribute("cy"));
                    break;

                case "blip":
                    var embed = reader.GetAttribute("embed", RelationshipsNs);
                    var link = reader.GetAttribute("link", RelationshipsNs);
                    if (embed is not null)
                    {
                        if (frame is not null && IsFill(frame, Parent(1), Parent(2)))
                        {
                            frame.Embeds.Add(embed);
                        }
                        else
                        {
                            Get(embed).OtherBlips++;
                        }
                    }

                    if (link is not null)
                    {
                        Get(link).OtherBlips++;
                    }

                    break;

                case "xfrm" when frame is { Kind: not FrameKind.Background } && Parent(1) == "spPr":
                    // A quarter turn swaps the displayed width and height.
                    var degrees = Math.Abs(ParseDouble(reader.GetAttribute("rot")) / 60000d) % 180d;
                    frame.Rotated = degrees is > 45d and < 135d;
                    break;

                case "ext" when frame is { Kind: not FrameKind.Background, HasExtent: false } && Parent(1) == "xfrm" && Parent(2) == "spPr":
                    frame.Cx = ParseDouble(reader.GetAttribute("cx"));
                    frame.Cy = ParseDouble(reader.GetAttribute("cy"));
                    frame.HasExtent = frame.Cx > 0 && frame.Cy > 0;
                    break;

                case "srcRect" when frame is not null && Parent(1) == "blipFill":
                    var left = ParseDouble(reader.GetAttribute("l"));
                    var right = ParseDouble(reader.GetAttribute("r"));
                    var top = ParseDouble(reader.GetAttribute("t"));
                    var bottom = ParseDouble(reader.GetAttribute("b"));
                    // Percentages in 1/1000 %. Negative values pad the picture; treated as unknown at commit.
                    frame.CropX = 1 - (left + right) / 100000d;
                    frame.CropY = 1 - (top + bottom) / 100000d;
                    break;

                case "tile" when frame is not null && Parent(1) == "blipFill":
                    // A tiled fill repeats the picture at its own scale: the shape's size says nothing about the pixels needed.
                    frame.Unknown = true;
                    break;

                case "fillRect" when frame is not null && Parent(1) == "stretch":
                    // An inset fill rectangle stretches the picture over a different area than the shape's box.
                    if (ParseDouble(reader.GetAttribute("l")) != 0 || ParseDouble(reader.GetAttribute("t")) != 0
                        || ParseDouble(reader.GetAttribute("r")) != 0 || ParseDouble(reader.GetAttribute("b")) != 0)
                    {
                        frame.Unknown = true;
                    }

                    break;
            }

            if (!empty)
            {
                path.Add(name);
            }
            else if (frame is not null && depth == frame.Depth && name == frame.Element)
            {
                Commit(frame, Get);
                frame = null;
            }
        }

        return placements;
    }

    private static bool IsGroup(string localName) => localName is "grpSp" or "wgp";

    private static bool IsPicture(string ns) => ns is PresentationNs or SpreadsheetDrawingNs or PictureNs;

    private static bool IsShape(string ns) => ns is PresentationNs or SpreadsheetDrawingNs or WordShapeNs;

    /// <summary>Only a stretched fill (not a bullet, effect or texture) says how large the picture is shown.</summary>
    private static bool IsFill(Frame frame, string parent, string grandparent) => parent == "blipFill" && frame.Kind switch
    {
        FrameKind.Picture => true,
        FrameKind.Shape => grandparent == "spPr",
        _ => grandparent == "bgPr"
    };

    private static Frame StartFrame(FrameKind kind, string element, int depth, Stack<Group> groups)
    {
        var frame = new Frame { Kind = kind, Depth = depth, Element = element, InGroup = groups.Count > 0 };
        foreach (var group in groups)
        {
            if (!group.Known)
            {
                frame.Unknown = true;
                break;
            }

            frame.ScaleX *= group.ExtCx / group.ChildCx;
            frame.ScaleY *= group.ExtCy / group.ChildCy;
        }

        return frame;
    }

    private static void Commit(Frame frame, Func<string, Placement> get)
    {
        foreach (var id in frame.Embeds)
        {
            var placement = get(id);
            // A rotated picture inside a group would have to be scaled along its own axes, not the group's.
            if (frame.Unknown || !frame.HasExtent || (frame.InGroup && frame.Rotated) || frame.CropX < 0.05 || frame.CropY < 0.05 || frame.CropX > 1 || frame.CropY > 1)
            {
                placement.Unknown = true;
                continue;
            }

            var width = frame.Cx * frame.ScaleX / EmuPerInch / frame.CropX;
            var height = frame.Cy * frame.ScaleY / EmuPerInch / frame.CropY;
            if (frame.Rotated)
            {
                (width, height) = (height, width);
            }

            placement.WidthInches = Math.Max(placement.WidthInches, width);
            placement.HeightInches = Math.Max(placement.HeightInches, height);
            placement.Pictures++;
        }
    }

    private static double ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
}
