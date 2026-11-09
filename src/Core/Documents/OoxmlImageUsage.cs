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
/// reported as downscalable when every reference is a plain, ungrouped picture with a known size - anything else (shape
/// fills, backgrounds, group transforms, VML) leaves it at full resolution.
/// </summary>
internal static class OoxmlImageUsage
{
    private const double EmuPerInch = 914400d;

    private const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

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
                    byRelationshipId = ScanDrawingPart(sourceEntry);
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

    private sealed class PictureFrame
    {
        public readonly List<string> Embeds = new();
        public double Cx;
        public double Cy;
        public bool HasExtent;
        public bool Rotated;
        public double CropX = 1;
        public double CropY = 1;
        public bool InGroup;
    }

    private static Dictionary<string, Placement> ScanDrawingPart(ZipArchiveEntry entry)
    {
        var placements = new Dictionary<string, Placement>(StringComparer.Ordinal);
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false });

        PictureFrame? frame = null;
        var picDepth = -1;
        var groupDepth = 0;
        var xfrmDepth = -1;
        var inGroupStack = new Stack<int>();

        Placement Get(string id)
        {
            if (!placements.TryGetValue(id, out var p))
            {
                p = new Placement();
                placements[id] = p;
            }

            return p;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "grpSp" && inGroupStack.Count > 0)
                {
                    inGroupStack.Pop();
                    groupDepth--;
                }
                else if (reader.LocalName == "xfrm" && reader.Depth == xfrmDepth)
                {
                    xfrmDepth = -1;
                }
                else if (frame is not null && reader.LocalName == "pic" && reader.Depth == picDepth)
                {
                    Commit(frame, Get);
                    frame = null;
                    picDepth = -1;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "grpSp":
                    if (!reader.IsEmptyElement)
                    {
                        inGroupStack.Push(reader.Depth);
                        groupDepth++;
                    }

                    break;

                case "pic" when frame is null:
                    frame = new PictureFrame { InGroup = groupDepth > 0 };
                    picDepth = reader.Depth;
                    if (reader.IsEmptyElement)
                    {
                        Commit(frame, Get);
                        frame = null;
                        picDepth = -1;
                    }

                    break;

                case "blip":
                    var embed = reader.GetAttribute("embed", RelationshipsNs);
                    var link = reader.GetAttribute("link", RelationshipsNs);
                    if (embed is not null)
                    {
                        if (frame is not null)
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

                case "xfrm" when frame is not null:
                    if (!reader.IsEmptyElement)
                    {
                        xfrmDepth = reader.Depth;
                    }

                    var rot = ParseDouble(reader.GetAttribute("rot"));
                    // 60000ths of a degree; a quarter turn swaps the displayed width and height.
                    var degrees = Math.Abs(rot / 60000d) % 180d;
                    frame.Rotated = degrees is > 45d and < 135d;
                    break;

                case "ext" when frame is not null && !frame.HasExtent && xfrmDepth >= 0:
                    frame.Cx = ParseDouble(reader.GetAttribute("cx"));
                    frame.Cy = ParseDouble(reader.GetAttribute("cy"));
                    frame.HasExtent = frame.Cx > 0 && frame.Cy > 0;
                    break;

                case "srcRect" when frame is not null:
                    var left = ParseDouble(reader.GetAttribute("l"));
                    var right = ParseDouble(reader.GetAttribute("r"));
                    var top = ParseDouble(reader.GetAttribute("t"));
                    var bottom = ParseDouble(reader.GetAttribute("b"));
                    // Percentages in 1/1000 %. Negative values pad the picture; treat those as unknown below.
                    frame.CropX = 1 - (left + right) / 100000d;
                    frame.CropY = 1 - (top + bottom) / 100000d;
                    break;
            }
        }

        return placements;
    }

    private static void Commit(PictureFrame frame, Func<string, Placement> get)
    {
        foreach (var id in frame.Embeds)
        {
            var placement = get(id);
            if (frame.InGroup || !frame.HasExtent || frame.CropX < 0.05 || frame.CropY < 0.05 || frame.CropX > 1 || frame.CropY > 1)
            {
                placement.Unknown = true;
                continue;
            }

            var width = frame.Cx / EmuPerInch / frame.CropX;
            var height = frame.Cy / EmuPerInch / frame.CropY;
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
