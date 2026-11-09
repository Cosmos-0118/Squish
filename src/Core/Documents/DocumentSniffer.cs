using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Squish.Core.Documents;

internal enum DocumentFormat
{
    Unknown,

    /// <summary>Office Open XML: docx, xlsx, pptx and their macro-enabled siblings.</summary>
    Ooxml,

    /// <summary>OpenDocument: odt, ods, odp.</summary>
    OpenDocument,

    Epub
}

internal enum DocumentSniffOutcome
{
    Supported,
    NotAZip,

    /// <summary>OLE2 compound file: legacy .doc/.xls/.ppt, or an encrypted/IRM-protected OOXML package.</summary>
    LegacyOrEncrypted,
    Unrecognised,

    /// <summary>Digitally signed or encrypted: any rewrite would invalidate the signature or the payload.</summary>
    Protected,
    Malformed
}

internal readonly record struct DocumentSniffResult(DocumentSniffOutcome Outcome, DocumentFormat Format, string? Detail = null)
{
    public bool IsSupported => Outcome == DocumentSniffOutcome.Supported;
}

/// <summary>
/// Decides whether a file is a ZIP-based document Squish may rewrite, by content rather than extension.
/// Files that cannot be rewritten without losing something (signatures, encryption, OLE2) are reported, never touched.
/// </summary>
internal static class DocumentSniffer
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "xlsx", "xlsm", "docx", "docm", "pptx", "pptm", "ods", "odt", "odp", "epub"
    };

    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Ole2Magic = [0xD0, 0xCF, 0x11, 0xE0];

    /// <summary>Microsoft Office writes a hidden <c>~$name.docx</c> next to every open document.</summary>
    public static bool IsOfficeLockFile(string path)
        => Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);

    /// <summary>
    /// Word, Excel and PowerPoint drop a hidden <c>~$</c> file next to a document while it is open (the first two characters of the
    /// name are replaced for longer names); LibreOffice writes <c>.~lock.name#</c>. Rewriting a document under its editor loses
    /// the editor's changes or fails on the sharing violation, so such files are left alone.
    /// </summary>
    public static bool IsOpenInAnApplication(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
        {
            return false;
        }

        var candidates = new List<string> { "~$" + name, ".~lock." + name + "#" };
        if (name.Length > 2)
        {
            candidates.Add("~$" + name[2..]);
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(directory, candidate)))
            {
                return true;
            }
        }

        return false;
    }

    public static DocumentSniffResult Sniff(string path)
    {
        if (IsOfficeLockFile(path))
        {
            return new DocumentSniffResult(DocumentSniffOutcome.Unrecognised, DocumentFormat.Unknown, "Office lock file");
        }

        var head = new byte[4];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Read(head, 0, 4) < 4)
        {
            return new DocumentSniffResult(DocumentSniffOutcome.NotAZip, DocumentFormat.Unknown);
        }

        if (head.AsSpan().SequenceEqual(Ole2Magic))
        {
            return new DocumentSniffResult(DocumentSniffOutcome.LegacyOrEncrypted, DocumentFormat.Unknown, "Legacy binary or password-protected Office file");
        }

        if (!head.AsSpan().SequenceEqual(ZipMagic))
        {
            return new DocumentSniffResult(DocumentSniffOutcome.NotAZip, DocumentFormat.Unknown);
        }

        try
        {
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return Classify(archive);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return new DocumentSniffResult(DocumentSniffOutcome.Malformed, DocumentFormat.Unknown, ex.Message);
        }
    }

    internal static DocumentSniffResult Classify(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName))
            {
                return new DocumentSniffResult(DocumentSniffOutcome.Malformed, DocumentFormat.Unknown, "Duplicate entry names");
            }
        }

        if (names.Contains("[Content_Types].xml"))
        {
            foreach (var name in names)
            {
                if (name.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase))
                {
                    return new DocumentSniffResult(DocumentSniffOutcome.Protected, DocumentFormat.Ooxml, "Digitally signed document");
                }
            }

            return new DocumentSniffResult(DocumentSniffOutcome.Supported, DocumentFormat.Ooxml);
        }

        if (names.Contains("META-INF/manifest.xml") && names.Contains("mimetype"))
        {
            if (names.Contains("META-INF/documentsignatures.xml"))
            {
                return new DocumentSniffResult(DocumentSniffOutcome.Protected, DocumentFormat.OpenDocument, "Digitally signed document");
            }

            var manifest = ReadSmallText(archive, "META-INF/manifest.xml");
            if (manifest is not null && manifest.Contains("encryption-data", StringComparison.Ordinal))
            {
                return new DocumentSniffResult(DocumentSniffOutcome.Protected, DocumentFormat.OpenDocument, "Encrypted document");
            }

            return new DocumentSniffResult(DocumentSniffOutcome.Supported, DocumentFormat.OpenDocument);
        }

        if (names.Contains("META-INF/container.xml") && names.Contains("mimetype"))
        {
            if (names.Contains("META-INF/encryption.xml"))
            {
                // Font obfuscation is harmless (those bytes are copied untouched); DRM is not.
                var encryption = ReadSmallText(archive, "META-INF/encryption.xml") ?? string.Empty;
                var stripped = encryption
                    .Replace("http://www.idpf.org/2008/embedding", string.Empty, StringComparison.Ordinal)
                    .Replace("http://ns.adobe.com/pdf/enc#RC", string.Empty, StringComparison.Ordinal);
                if (stripped.Contains("EncryptionMethod", StringComparison.Ordinal)
                    && stripped.Contains("Algorithm=\"http", StringComparison.Ordinal))
                {
                    return new DocumentSniffResult(DocumentSniffOutcome.Protected, DocumentFormat.Epub, "DRM-protected book");
                }
            }

            if (names.Contains("META-INF/signatures.xml"))
            {
                return new DocumentSniffResult(DocumentSniffOutcome.Protected, DocumentFormat.Epub, "Digitally signed book");
            }

            return new DocumentSniffResult(DocumentSniffOutcome.Supported, DocumentFormat.Epub);
        }

        return new DocumentSniffResult(DocumentSniffOutcome.Unrecognised, DocumentFormat.Unknown);
    }

    private static string? ReadSmallText(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null || entry.Length > 4 * 1024 * 1024)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
