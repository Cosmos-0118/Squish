using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace Squish.Core.Documents;

/// <summary>What the media folders of an Office package carry that nothing needs: files no relationship points to, and copies of files.</summary>
internal sealed record MediaPlan(IReadOnlySet<string> Unreferenced, IReadOnlyDictionary<string, string> Duplicates)
{
    public static MediaPlan Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>
/// Finds dead weight in <c>word/media</c>, <c>ppt/media</c> and <c>xl/media</c>. Everything an OPC package uses is reached through
/// a relationship, so a media part that no relationship names is unreachable, and two parts with the same bytes in the same folder
/// can be one part that both relationships name.
/// </summary>
internal static class OoxmlMediaPlanner
{
    private static readonly string[] MediaFolders = ["word/media/", "ppt/media/", "xl/media/"];

    public static bool IsMediaPart(string entryName)
    {
        if (entryName.EndsWith('/'))
        {
            return false;
        }

        foreach (var folder in MediaFolders)
        {
            if (entryName.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static MediaPlan Plan(ZipArchive archive)
    {
        var media = new List<ZipArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            if (IsMediaPart(entry.FullName))
            {
                media.Add(entry);
            }
        }

        if (media.Count == 0)
        {
            return MediaPlan.Empty;
        }

        var referenced = ReadReferencedParts(archive);
        if (referenced is null)
        {
            // One relationship part that cannot be read could be the one that names a picture: change nothing.
            return MediaPlan.Empty;
        }

        var unreferenced = new HashSet<string>(StringComparer.Ordinal);
        var live = new List<ZipArchiveEntry>();
        foreach (var entry in media)
        {
            if (referenced.Contains(entry.FullName))
            {
                live.Add(entry);
            }
            else
            {
                unreferenced.Add(entry.FullName);
            }
        }

        return new MediaPlan(unreferenced, FindDuplicates(live));
    }

    /// <summary>Every internal relationship target in the package, or null when any relationship part is unreadable.</summary>
    private static HashSet<string>? ReadReferencedParts(ZipArchive archive)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!PackagePaths.IsRelationshipsPart(entry.FullName))
            {
                continue;
            }

            try
            {
                using var stream = entry.Open();
                foreach (var relationship in RelationshipReader.Read(stream, entry.FullName))
                {
                    referenced.Add(relationship.ResolvedTarget);
                }
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
            {
                return null;
            }
        }

        return referenced;
    }

    /// <summary>Duplicate part name to the part that stays (the first in archive order), within one folder and extension.</summary>
    private static Dictionary<string, string> FindDuplicates(List<ZipArchiveEntry> live)
    {
        var duplicates = new Dictionary<string, string>(StringComparer.Ordinal);

        // Length and the stored CRC are free; only parts that agree on both are read to compare their content.
        var candidates = new Dictionary<(string Folder, string Extension, long Length, uint Crc), List<ZipArchiveEntry>>();
        foreach (var entry in live)
        {
            if (entry.Length == 0)
            {
                continue;
            }

            var name = entry.FullName;
            var key = (name[..(name.LastIndexOf('/') + 1)].ToLowerInvariant(), PackagePaths.ExtensionOf(name), entry.Length, entry.Crc32);
            if (!candidates.TryGetValue(key, out var group))
            {
                group = new List<ZipArchiveEntry>();
                candidates[key] = group;
            }

            group.Add(entry);
        }

        foreach (var group in candidates.Values)
        {
            if (group.Count < 2)
            {
                continue;
            }

            var byHash = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in group)
            {
                string hash;
                using (var stream = entry.Open())
                {
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }

                if (byHash.TryGetValue(hash, out var keeper))
                {
                    duplicates[entry.FullName] = keeper.FullName;
                }
                else
                {
                    byHash[hash] = entry;
                }
            }
        }

        return duplicates;
    }
}
