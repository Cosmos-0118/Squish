using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace Squish.Core.Documents;

/// <summary>
/// Streaming XML copy with optional minification and per-element hooks. Memory use is bounded by the nesting depth, so
/// multi-hundred-megabyte worksheets are safe. DTDs are prohibited (no XXE, no entity expansion): a part that carries
/// one throws <see cref="XmlException"/> and the caller keeps the original bytes.
/// </summary>
internal class XmlPartRewriter
{
    // Elements whose text is the document's content: whitespace inside them is never indentation.
    private static readonly HashSet<string> TextLeaves = new(StringComparer.Ordinal)
    {
        "t", "v", "f", "delText", "instrText", "delInstrText", "text", "formula", "formula1", "formula2", "definedName"
    };

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = false,
        IgnoreWhitespace = false,
        CheckCharacters = false
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool Minify { get; init; } = true;

    public void Rewrite(Stream input, Stream output)
    {
        using var reader = XmlReader.Create(input, ReaderSettings);
        var writerSettings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = false,
            NewLineHandling = NewLineHandling.Entitize,
            CloseOutput = false,
            CheckCharacters = false,
            OmitXmlDeclaration = false
        };

        using var writer = XmlWriter.Create(output, writerSettings);
        Run(reader, writer);
        writer.Flush();
    }

    private void Run(XmlReader reader, XmlWriter writer)
    {
        var open = new Stack<string>();
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.XmlDeclaration:
                    WriteDeclaration(reader, writer);
                    break;

                case XmlNodeType.Element:
                    if (!OnElement(reader, writer))
                    {
                        var localName = reader.LocalName;
                        var isEmpty = reader.IsEmptyElement;
                        CopyElement(reader, writer);
                        if (!isEmpty)
                        {
                            open.Push(localName);
                            AfterStartElement(localName, writer);
                        }
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (open.Count > 0)
                    {
                        open.Pop();
                    }

                    writer.WriteEndElement();
                    break;

                case XmlNodeType.Text:
                    writer.WriteString(reader.Value);
                    break;

                case XmlNodeType.CDATA:
                    writer.WriteCData(reader.Value);
                    break;

                case XmlNodeType.SignificantWhitespace:
                    writer.WriteString(reader.Value);
                    break;

                case XmlNodeType.Whitespace:
                    if (!Minify || IsSignificantWhitespace(reader, open))
                    {
                        writer.WriteString(reader.Value);
                    }

                    break;

                case XmlNodeType.Comment:
                    if (!Minify)
                    {
                        writer.WriteComment(reader.Value);
                    }

                    break;

                case XmlNodeType.ProcessingInstruction:
                    writer.WriteProcessingInstruction(reader.Name, reader.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// Return true after writing (or deliberately omitting, via <see cref="XmlReader.Skip"/>) the current element and
    /// everything inside it. Return false to let the default copy happen.
    /// </summary>
    protected virtual bool OnElement(XmlReader reader, XmlWriter writer) => false;

    /// <summary>Called right after the start tag of a copied, non-empty element was written; may add leading children.</summary>
    protected virtual void AfterStartElement(string localName, XmlWriter writer)
    {
    }

    /// <summary>Consumes the current element and everything inside it without writing anything.</summary>
    protected static void SkipElement(XmlReader reader)
    {
        using var subtree = reader.ReadSubtree();
        while (subtree.Read())
        {
        }
    }

    /// <summary>Copies the start tag (and its attributes). Empty elements are closed immediately.</summary>
    protected static void CopyElement(XmlReader reader, XmlWriter writer)
    {
        writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
        writer.WriteAttributes(reader, defattr: true);
        reader.MoveToElement();
        if (reader.IsEmptyElement)
        {
            writer.WriteEndElement();
        }
    }

    private static void WriteDeclaration(XmlReader reader, XmlWriter writer)
    {
        var standalone = reader.GetAttribute("standalone");
        if (standalone is null)
        {
            writer.WriteStartDocument();
        }
        else
        {
            writer.WriteStartDocument(string.Equals(standalone, "yes", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool IsSignificantWhitespace(XmlReader reader, Stack<string> open)
    {
        if (reader.XmlSpace == XmlSpace.Preserve)
        {
            return true;
        }

        // Only indentation (whitespace with a line break) is dropped; a lone space between elements may be real text.
        var value = reader.Value;
        if (value.AsSpan().IndexOfAny('\n', '\r') < 0)
        {
            return true;
        }

        return open.Count > 0 && TextLeaves.Contains(open.Peek());
    }
}
