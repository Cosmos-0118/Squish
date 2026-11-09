using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace Squish.Core.Documents;

/// <summary>
/// Last line of defence before a rewritten document replaces the original: the archive must still be a well-formed zip, every
/// XML part must parse, and no relationship or content-type override may point at a part that is not there (unless it was
/// already dangling in the source, which is not ours to fix).
/// </summary>
internal static class DocumentPackageValidator
{
    private static readonly XmlReaderSettings ParseSettings = new()
    {
        // Validation only needs well-formedness; ignoring the DOCTYPE keeps NCX/XHTML files valid without resolving anything.
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CheckCharacters = false
    };

    /// <summary>Relationship targets and overrides that already pointed nowhere in the source package.</summary>
    public static HashSet<string> FindDangling(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            names.Add(entry.FullName);
        }

        var dangling = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            try
            {
                if (PackagePaths.IsRelationshipsPart(entry.FullName))
                {
                    using var stream = entry.Open();
                    foreach (var relationship in RelationshipReader.Read(stream, entry.FullName))
                    {
                        if (!names.Contains(relationship.ResolvedTarget))
                        {
                            dangling.Add(entry.FullName + "|" + relationship.ResolvedTarget);
                        }
                    }
                }
                else if (entry.FullName == PackagePaths.ContentTypesPart)
                {
                    using var stream = entry.Open();
                    var (_, overrides) = ContentTypesRewriter.Read(stream);
                    foreach (var part in overrides)
                    {
                        if (!names.Contains(part))
                        {
                            dangling.Add(PackagePaths.ContentTypesPart + "|" + part);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException)
            {
                dangling.Add(entry.FullName + "|<unreadable>");
            }
        }

        return dangling;
    }

    /// <returns>null when the package is sound; otherwise what is wrong with it.</returns>
    public static string? Validate(string path, DocumentFormat format, IReadOnlySet<string> sourceDangling, IReadOnlySet<string> rewrittenParts)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (!names.Add(entry.FullName))
                {
                    return $"Duplicate entry {entry.FullName}";
                }
            }

            if (format != DocumentFormat.Ooxml)
            {
                var first = archive.Entries.Count > 0 ? archive.Entries[0] : null;
                if (first is null || first.FullName != "mimetype" || first.CompressedLength != first.Length)
                {
                    return "mimetype must be the first, uncompressed entry";
                }
            }

            foreach (var entry in archive.Entries)
            {
                // Parts copied byte for byte keep whatever state they were in (some real files carry empty .xml parts);
                // only what this tool wrote has to parse.
                if (!IsXml(entry.FullName) || !rewrittenParts.Contains(entry.FullName))
                {
                    // Reading to the end verifies the deflate stream and CRC of binary parts too.
                    using var raw = entry.Open();
                    raw.CopyTo(Stream.Null);
                    continue;
                }

                using var xml = entry.Open();
                using var reader = XmlReader.Create(xml, ParseSettings);
                while (reader.Read())
                {
                }
            }

            if (format == DocumentFormat.Ooxml)
            {
                if (!names.Contains(PackagePaths.ContentTypesPart))
                {
                    return "[Content_Types].xml is missing";
                }

                foreach (var item in FindDangling(archive))
                {
                    if (!sourceDangling.Contains(item))
                    {
                        return $"Dangling reference {item}";
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    private static bool IsXml(string name)
        => name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase);
}
