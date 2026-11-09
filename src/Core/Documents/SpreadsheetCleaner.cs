using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Squish.Core.Documents;

/// <summary>
/// Which cell formats (indexes into <c>cellXfs</c>) draw something on an otherwise empty cell. A format with no fill and
/// no border renders exactly like a missing cell, so an empty cell that uses one carries no information.
/// </summary>
internal sealed class SpreadsheetStyles
{
    private readonly bool[] _visible;

    private SpreadsheetStyles(bool[] visible) => _visible = visible;

    /// <summary>Unknown styles are treated as visible so nothing is dropped on a guess.</summary>
    public bool IsVisible(int styleIndex) => styleIndex < 0 || styleIndex >= _visible.Length || _visible[styleIndex];

    public static SpreadsheetStyles Unknown { get; } = new([]);

    public static SpreadsheetStyles Parse(Stream stream)
    {
        var styleXfs = new List<bool>();
        var cellXfs = new List<(bool Visible, int ParentIndex)>();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false });

        string? section = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName is "cellStyleXfs" or "cellXfs")
                {
                    section = reader.LocalName;
                }
                else if (reader.LocalName == "xf" && section is not null)
                {
                    var visible = ParseInt(reader.GetAttribute("fillId")) != 0 || ParseInt(reader.GetAttribute("borderId")) != 0;
                    if (section == "cellStyleXfs")
                    {
                        styleXfs.Add(visible);
                    }
                    else
                    {
                        cellXfs.Add((visible, ParseInt(reader.GetAttribute("xfId"))));
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "cellStyleXfs" or "cellXfs")
            {
                section = null;
            }
        }

        var result = new bool[cellXfs.Count];
        for (var i = 0; i < cellXfs.Count; i++)
        {
            var (visible, parent) = cellXfs[i];
            result[i] = visible || (parent >= 0 && parent < styleXfs.Count && styleXfs[parent]);
        }

        return new SpreadsheetStyles(result);
    }

    private static int ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

internal sealed class SheetScan
{
    public int LastContentRow { get; set; }

    public int LastMergedRow { get; set; }

    /// <summary>Worksheets that omit cell references (implicit positions) are left alone.</summary>
    public bool HasImplicitReferences { get; set; }
}

internal sealed class SheetCleanStats
{
    public long DroppedCells { get; set; }

    public long DroppedRows { get; set; }

    public int MinRow { get; set; } = int.MaxValue;

    public int MaxRow { get; set; }

    public int MinColumn { get; set; } = int.MaxValue;

    public int MaxColumn { get; set; }

    public bool HasImplicitReferences { get; set; }

    public bool AnyDropped => DroppedCells > 0 || DroppedRows > 0;

    public string? DimensionRef => HasImplicitReferences
        ? null
        : MaxRow == 0
            ? "A1"
            : MinRow == MaxRow && MinColumn == MaxColumn
                ? SpreadsheetCleaner.CellName(MinColumn, MinRow)
                : SpreadsheetCleaner.CellName(MinColumn, MinRow) + ":" + SpreadsheetCleaner.CellName(MaxColumn, MaxRow);

    public void Include(int column, int row)
    {
        MinRow = Math.Min(MinRow, row);
        MaxRow = Math.Max(MaxRow, row);
        MinColumn = Math.Min(MinColumn, column);
        MaxColumn = Math.Max(MaxColumn, column);
    }
}

/// <summary>
/// Streams a worksheet and removes cells and rows that exist only as bloat: empty cells whose format draws nothing and
/// rows left without cells. In aggressive mode empty-but-formatted cells below the last real row go too. The
/// <c>dimension</c> element is rewritten to the data that is left.
/// </summary>
internal sealed class SpreadsheetCleaner : XmlPartRewriter
{
    private readonly SpreadsheetStyles _styles;
    private readonly bool _aggressive;
    private readonly int _cutoffRow;
    private readonly string? _dimensionOverride;
    private readonly List<(int Min, int Max, int Style)> _columnStyles = new();

    public SpreadsheetCleaner(SpreadsheetStyles styles, bool aggressive, int cutoffRow, string? dimensionOverride)
    {
        _styles = styles;
        _aggressive = aggressive;
        _cutoffRow = cutoffRow;
        _dimensionOverride = dimensionOverride;
    }

    public SheetCleanStats Stats { get; } = new();

    // ---- pass 1: where does real data end? ---------------------------------------------------------------

    public static SheetScan Scan(Stream stream)
    {
        var scan = new SheetScan();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "c":
                    if (!TryParseCellRef(reader.GetAttribute("r"), out _, out var row))
                    {
                        scan.HasImplicitReferences = true;
                    }
                    else if (!reader.IsEmptyElement || reader.GetAttribute("cm") is not null || reader.GetAttribute("vm") is not null)
                    {
                        scan.LastContentRow = Math.Max(scan.LastContentRow, row);
                    }

                    break;
                case "mergeCell":
                    var reference = reader.GetAttribute("ref");
                    var colon = reference?.IndexOf(':', StringComparison.Ordinal) ?? -1;
                    if (reference is not null && colon > 0 && TryParseCellRef(reference[(colon + 1)..], out _, out var mergedRow))
                    {
                        scan.LastMergedRow = Math.Max(scan.LastMergedRow, mergedRow);
                    }

                    break;
            }
        }

        return scan;
    }

    // ---- pass 2/3: rewrite ------------------------------------------------------------------------------

    protected override bool OnElement(XmlReader reader, XmlWriter writer)
    {
        switch (reader.LocalName)
        {
            case "col":
                RecordColumnStyle(reader);
                return false;

            case "dimension" when _dimensionOverride is not null:
                writer.WriteStartElement(reader.Prefix, "dimension", reader.NamespaceURI);
                writer.WriteAttributeString("ref", _dimensionOverride);
                writer.WriteEndElement();
                SkipElement(reader);
                return true;

            case "row" when !reader.IsEmptyElement:
                RewriteRow(reader, writer);
                return true;

            case "row":
                RewriteEmptyRow(reader, writer);
                return true;
        }

        return false;
    }

    private void RecordColumnStyle(XmlReader reader)
    {
        if (!int.TryParse(reader.GetAttribute("style"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var style) || style == 0)
        {
            return;
        }

        _ = int.TryParse(reader.GetAttribute("min"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min);
        _ = int.TryParse(reader.GetAttribute("max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max);
        if (_styles.IsVisible(style))
        {
            _columnStyles.Add((min, max, style));
        }
    }

    private bool ColumnHasVisibleStyle(int column)
    {
        foreach (var (min, max, _) in _columnStyles)
        {
            if (column >= min && column <= max)
            {
                return true;
            }
        }

        return false;
    }

    private void RewriteEmptyRow(XmlReader reader, XmlWriter writer)
    {
        var attributes = CaptureAttributes(reader);
        reader.MoveToElement();
        if (!HasMeaningfulRowAttributes(attributes))
        {
            Stats.DroppedRows++;
            return;
        }

        WriteRowStart(writer, reader.Prefix, reader.LocalName, reader.NamespaceURI, attributes);
        writer.WriteEndElement();
    }

    private void RewriteRow(XmlReader reader, XmlWriter writer)
    {
        var prefix = reader.Prefix;
        var localName = reader.LocalName;
        var ns = reader.NamespaceURI;
        var attributes = CaptureAttributes(reader);
        reader.MoveToElement();

        _ = int.TryParse(Find(attributes, "r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowNumber);
        var rowStyleVisible = IsTrue(Find(attributes, "customFormat"))
            && int.TryParse(Find(attributes, "s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowStyle)
            && _styles.IsVisible(rowStyle);
        var started = false;
        var pastData = _aggressive && rowNumber > _cutoffRow;

        using var row = reader.ReadSubtree();
        row.Read(); // the row element itself
        var more = row.Read();
        while (more)
        {
            if (row.NodeType == XmlNodeType.Element && row.LocalName == "c" && row.Depth == 1)
            {
                var keep = ShouldKeepCell(row, rowStyleVisible, pastData, out var column, out var cellRow);
                if (!keep)
                {
                    Stats.DroppedCells++;
                    SkipElement(row);
                    more = row.Read();
                    continue;
                }

                if (!started)
                {
                    WriteRowStart(writer, prefix, localName, ns, attributes);
                    started = true;
                }

                if (column > 0)
                {
                    Stats.Include(column, cellRow > 0 ? cellRow : rowNumber);
                }
                else
                {
                    Stats.HasImplicitReferences = true;
                }

                writer.WriteNode(row, defattr: true);
                // WriteNode leaves the reader on the node after the element.
                more = !row.EOF;
                continue;
            }

            if (row.NodeType == XmlNodeType.Element)
            {
                // Anything that is not a cell (extLst and friends) keeps the row alive and is copied as is.
                if (!started)
                {
                    WriteRowStart(writer, prefix, localName, ns, attributes);
                    started = true;
                }

                writer.WriteNode(row, defattr: true);
                more = !row.EOF;
                continue;
            }

            more = row.Read();
        }

        if (started)
        {
            writer.WriteEndElement();
        }
        else if (HasMeaningfulRowAttributes(attributes))
        {
            WriteRowStart(writer, prefix, localName, ns, attributes);
            writer.WriteEndElement();
        }
        else
        {
            Stats.DroppedRows++;
        }
    }

    private bool ShouldKeepCell(XmlReader cell, bool rowStyleVisible, bool pastData, out int column, out int row)
    {
        column = 0;
        row = 0;
        if (!TryParseCellRef(cell.GetAttribute("r"), out column, out row))
        {
            column = 0;
            return true;
        }

        var hasContent = !cell.IsEmptyElement || cell.GetAttribute("cm") is not null || cell.GetAttribute("vm") is not null;
        if (hasContent)
        {
            return true;
        }

        var style = int.TryParse(cell.GetAttribute("s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var visible = _styles.IsVisible(style);
        if (visible)
        {
            // A formatted blank (fill, border): keep unless it sits below all data and the user asked for aggressive.
            return !pastData;
        }

        // The format draws nothing, but without this element the cell would pick up its row's or column's format.
        return rowStyleVisible || ColumnHasVisibleStyle(column);
    }

    private static List<(string Prefix, string Local, string Ns, string Value)> CaptureAttributes(XmlReader reader)
    {
        var list = new List<(string, string, string, string)>();
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                list.Add((reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value));
            }
            while (reader.MoveToNextAttribute());
        }

        return list;
    }

    private static void WriteRowStart(XmlWriter writer, string prefix, string localName, string ns, List<(string Prefix, string Local, string Ns, string Value)> attributes)
    {
        writer.WriteStartElement(prefix, localName, ns);
        foreach (var (attrPrefix, local, attrNs, value) in attributes)
        {
            if (attrPrefix == "xmlns")
            {
                writer.WriteAttributeString("xmlns", local, null, value);
            }
            else if (local == "xmlns" && attrPrefix.Length == 0)
            {
                writer.WriteAttributeString("xmlns", value);
            }
            else
            {
                writer.WriteAttributeString(attrPrefix, local, attrNs.Length == 0 ? null : attrNs, value);
            }
        }
    }

    private static bool HasMeaningfulRowAttributes(List<(string Prefix, string Local, string Ns, string Value)> attributes)
    {
        foreach (var (_, local, _, _) in attributes)
        {
            if (local is not ("r" or "spans" or "dyDescent" or "xmlns"))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Find(List<(string Prefix, string Local, string Ns, string Value)> attributes, string local)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Local == local && attribute.Ns.Length == 0)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private static bool IsTrue(string? value) => value is "1" or "true";

    // ---- cell reference helpers -------------------------------------------------------------------------

    internal static bool TryParseCellRef(string? reference, out int column, out int row)
    {
        column = 0;
        row = 0;
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        var i = 0;
        while (i < reference.Length && (reference[i] is >= 'A' and <= 'Z' || reference[i] is >= 'a' and <= 'z'))
        {
            column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            i++;
            if (column > 20000)
            {
                return false;
            }
        }

        if (i == 0 || i == reference.Length)
        {
            return false;
        }

        return int.TryParse(reference.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out row) && row > 0;
    }

    internal static string CellName(int column, int row)
    {
        Span<char> letters = stackalloc char[8];
        var position = letters.Length;
        var n = column;
        while (n > 0)
        {
            n--;
            letters[--position] = (char)('A' + n % 26);
            n /= 26;
        }

        return string.Concat(letters[position..], row.ToString(CultureInfo.InvariantCulture));
    }
}
