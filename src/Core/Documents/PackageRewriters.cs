using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Squish.Core.Documents;

/// <summary>Part-name arithmetic for OPC packages (OOXML): relationship targets are relative to the owning part.</summary>
internal static class PackagePaths
{
    public const string ContentTypesPart = "[Content_Types].xml";

    public static bool IsRelationshipsPart(string entryName)
        => entryName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
           && (entryName.Contains("/_rels/", StringComparison.OrdinalIgnoreCase) || entryName.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The directory relationship targets are resolved against: <c>word/_rels/document.xml.rels</c> gives <c>word/</c>.</summary>
    public static string RelationshipBaseDirectory(string relsEntryName)
    {
        var marker = relsEntryName.LastIndexOf("_rels/", StringComparison.OrdinalIgnoreCase);
        return marker <= 0 ? string.Empty : relsEntryName[..marker];
    }

    /// <summary>The part a relationships file belongs to (<c>word/document.xml</c>), or empty for the package itself.</summary>
    public static string RelationshipSource(string relsEntryName)
    {
        var baseDir = RelationshipBaseDirectory(relsEntryName);
        var fileName = relsEntryName[(relsEntryName.LastIndexOf('/') + 1)..];
        if (fileName.Equals(".rels", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return baseDir + fileName[..^".rels".Length];
    }

    /// <summary>Resolves a relationship Target to a zip entry name (no leading slash, dot segments collapsed, percent escapes decoded).</summary>
    public static string ResolveTarget(string baseDirectory, string target)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            decoded = target;
        }

        var hash = decoded.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            decoded = decoded[..hash];
        }

        var combined = decoded.StartsWith('/') ? decoded[1..] : baseDirectory + decoded;
        var segments = new List<string>();
        foreach (var segment in combined.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    public static string ExtensionOf(string entryName)
    {
        var dot = entryName.LastIndexOf('.');
        var slash = entryName.LastIndexOf('/');
        return dot > slash && dot >= 0 ? entryName[(dot + 1)..].ToLowerInvariant() : string.Empty;
    }

    public static string WithExtension(string entryName, string newExtension)
    {
        var dot = entryName.LastIndexOf('.');
        var slash = entryName.LastIndexOf('/');
        return dot > slash && dot >= 0 ? entryName[..(dot + 1)] + newExtension : entryName + "." + newExtension;
    }

    public static string ContentTypeForImageExtension(string extension) => extension switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "tif" or "tiff" => "image/tiff",
        _ => "application/octet-stream"
    };
}

/// <summary>Reads (id, type, resolved target) triples from a relationships part, ignoring external links.</summary>
internal static class RelationshipReader
{
    public readonly record struct Relationship(string Id, string Type, string Target, string ResolvedTarget);

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CheckCharacters = false
    };

    public static List<Relationship> Read(Stream stream, string relsEntryName)
    {
        var baseDir = PackagePaths.RelationshipBaseDirectory(relsEntryName);
        var result = new List<Relationship>();
        using var reader = XmlReader.Create(stream, Settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
            {
                continue;
            }

            if (string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = reader.GetAttribute("Target");
            if (string.IsNullOrEmpty(target))
            {
                continue;
            }

            result.Add(new Relationship(
                reader.GetAttribute("Id") ?? string.Empty,
                reader.GetAttribute("Type") ?? string.Empty,
                target,
                PackagePaths.ResolveTarget(baseDir, target)));
        }

        return result;
    }
}

/// <summary>Drops relationships to removed parts and re-points the ones whose image part changed format.</summary>
internal sealed class RelationshipsRewriter : XmlPartRewriter
{
    private readonly string _baseDirectory;
    private readonly IReadOnlySet<string> _removedParts;
    private readonly IReadOnlyDictionary<string, string> _renamedParts;

    public RelationshipsRewriter(string relsEntryName, IReadOnlySet<string> removedParts, IReadOnlyDictionary<string, string> renamedParts)
    {
        _baseDirectory = PackagePaths.RelationshipBaseDirectory(relsEntryName);
        _removedParts = removedParts;
        _renamedParts = renamedParts;
    }

    protected override bool OnElement(XmlReader reader, XmlWriter writer)
    {
        if (reader.LocalName != "Relationship")
        {
            return false;
        }

        if (string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var target = reader.GetAttribute("Target");
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        var resolved = PackagePaths.ResolveTarget(_baseDirectory, target);
        if (_removedParts.Contains(resolved))
        {
            SkipElement(reader);
            return true;
        }

        if (!_renamedParts.TryGetValue(resolved, out var renamed))
        {
            return false;
        }

        writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.Name == "xmlns")
                {
                    writer.WriteAttributeString("xmlns", reader.Value);
                }
                else if (reader.Prefix == "xmlns")
                {
                    writer.WriteAttributeString("xmlns", reader.LocalName, null, reader.Value);
                }
                else if (reader.LocalName == "Target" && reader.NamespaceURI.Length == 0)
                {
                    writer.WriteAttributeString("Target", ReplaceFileName(target, renamed));
                }
                else
                {
                    writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI.Length == 0 ? null : reader.NamespaceURI, reader.Value);
                }
            }
            while (reader.MoveToNextAttribute());
        }

        reader.MoveToElement();
        SkipElement(reader);
        writer.WriteEndElement();
        return true;
    }

    /// <summary>Swaps the file name of a relative target for the new part's file name, keeping any folder prefix and escaping style.</summary>
    internal static string ReplaceFileName(string target, string newEntryName)
    {
        var newFile = newEntryName[(newEntryName.LastIndexOf('/') + 1)..];
        var slash = target.LastIndexOf('/');
        var escaped = target.Contains('%', StringComparison.Ordinal) ? Uri.EscapeDataString(newFile) : newFile;
        return slash < 0 ? escaped : target[..(slash + 1)] + escaped;
    }
}

/// <summary>Removes overrides for parts that were dropped and registers the default content type of newly created image formats.</summary>
internal sealed class ContentTypesRewriter : XmlPartRewriter
{
    private readonly IReadOnlySet<string> _removedParts;
    private readonly IReadOnlyDictionary<string, string> _missingDefaults;

    public ContentTypesRewriter(IReadOnlySet<string> removedParts, IReadOnlyDictionary<string, string> missingDefaults)
    {
        _removedParts = removedParts;
        _missingDefaults = missingDefaults;
    }

    protected override bool OnElement(XmlReader reader, XmlWriter writer)
    {
        if (reader.LocalName != "Override")
        {
            return false;
        }

        var partName = reader.GetAttribute("PartName");
        if (partName is null || !_removedParts.Contains(partName.TrimStart('/')))
        {
            return false;
        }

        SkipElement(reader);
        return true;
    }

    protected override void AfterStartElement(string localName, XmlWriter writer)
    {
        if (localName != "Types")
        {
            return;
        }

        foreach (var (extension, contentType) in _missingDefaults)
        {
            writer.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types");
            writer.WriteAttributeString("Extension", extension);
            writer.WriteAttributeString("ContentType", contentType);
            writer.WriteEndElement();
        }
    }

    public static (HashSet<string> DefaultExtensions, HashSet<string> OverrideParts) Read(Stream stream)
    {
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "Default" && reader.GetAttribute("Extension") is { } ext)
            {
                defaults.Add(ext);
            }
            else if (reader.LocalName == "Override" && reader.GetAttribute("PartName") is { } part)
            {
                overrides.Add(part.TrimStart('/'));
            }
        }

        return (defaults, overrides);
    }
}

/// <summary>Deletes elements by local name (used to scrub author and company details from document properties).</summary>
internal sealed class ElementRemovingRewriter : XmlPartRewriter
{
    private readonly IReadOnlySet<string> _localNames;

    public ElementRemovingRewriter(IReadOnlySet<string> localNames)
    {
        _localNames = localNames;
    }

    protected override bool OnElement(XmlReader reader, XmlWriter writer)
    {
        if (!_localNames.Contains(reader.LocalName))
        {
            return false;
        }

        SkipElement(reader);
        return true;
    }
}

/// <summary>Removes <c>file-entry</c> items from an ODF manifest whose full-path starts with one of the given prefixes.</summary>
internal sealed class OdfManifestRewriter : XmlPartRewriter
{
    private readonly IReadOnlyList<string> _removedPrefixes;

    public OdfManifestRewriter(IReadOnlyList<string> removedPrefixes)
    {
        _removedPrefixes = removedPrefixes;
    }

    protected override bool OnElement(XmlReader reader, XmlWriter writer)
    {
        if (reader.LocalName != "file-entry")
        {
            return false;
        }

        var path = reader.GetAttribute("full-path", reader.NamespaceURI);
        if (path is null)
        {
            return false;
        }

        foreach (var prefix in _removedPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                SkipElement(reader);
                return true;
            }
        }

        return false;
    }
}
