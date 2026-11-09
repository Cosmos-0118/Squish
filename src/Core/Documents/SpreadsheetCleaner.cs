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

internal static class SpreadsheetNamespaces
{
    public const string Transitional = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string Strict = "http://purl.oclc.org/ooxml/spreadsheetml/main";

    public static bool IsMain(string ns) => ns == Transitional || ns == Strict;
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
    private readonly bool _deferDimension;
    private readonly List<(int Min, int Max, int Style)> _columnStyles = new();
    private int _sheetDataDepth = -1;
    private double _defaultRowHeight = 15;
    private bool _placeholderWritten;

    /// <summary>
    /// Written in place of the <c>dimension</c> reference while rewriting: what survives is only known once the whole sheet
    /// has been read, so the caller swaps the real value in afterwards (see <see cref="SpliceDimension"/>).
    /// </summary>
    public const string DimensionPlaceholder = "@@SQUISH-DIMENSION@@";

    /// <param name="deferDimension">Write <see cref="DimensionPlaceholder"/> as the dimension and remember the original.</param>
    public SpreadsheetCleaner(SpreadsheetStyles styles, bool aggressive, int cutoffRow, bool deferDimension)
    {
        _styles = styles;
        _aggressive = aggressive;
        _cutoffRow = cutoffRow;
        _deferDimension = deferDimension;
    }

    public SheetCleanStats Stats { get; } = new();

    /// <summary>The <c>dimension</c> reference of the source sheet, when it has one.</summary>
    public string? OriginalDimension { get; private set; }

    /// <summary>
    /// What the dimension should say now: the data that is left when anything was dropped, otherwise exactly what the source said.
    /// </summary>
    public string? FinalDimension => Stats.AnyDropped && Stats.DimensionRef is { } computed ? computed : OriginalDimension;

    /// <summary>
    /// Copies <paramref name="rewritten"/> to <paramref name="destination"/> with the dimension placeholder replaced by the
    /// final value. The element sits before the sheet data, so only a small prefix of the output has to be searched.
    /// </summary>
    public void SpliceDimension(Stream rewritten, Stream destination)
    {
        rewritten.Position = 0;
        var needle = System.Text.Encoding.ASCII.GetBytes(DimensionPlaceholder);
        var window = new byte[(int)Math.Min(rewritten.Length, 256 * 1024)];
        var read = rewritten.ReadAtLeast(window, window.Length, throwOnEndOfStream: false);
        var at = window.AsSpan(0, read).IndexOf(needle);
        if (at < 0)
        {
            if (_placeholderWritten)
            {
                // The marker went out but cannot be found: shipping it would corrupt the sheet. The caller keeps the original part.
                throw new InvalidOperationException("Dimension placeholder not found in the rewritten sheet.");
            }

            // No dimension element in this sheet: nothing to swap.
            rewritten.Position = 0;
            rewritten.CopyTo(destination);
            return;
        }

        if (FinalDimension is not { } dimension)
        {
            throw new InvalidOperationException("Dimension placeholder without a value.");
        }

        destination.Write(window, 0, at);
        destination.Write(System.Text.Encoding.UTF8.GetBytes(System.Security.SecurityElement.Escape(dimension)));
        var tail = at + needle.Length;
        destination.Write(window, tail, read - tail);
        rewritten.CopyTo(destination);
    }

    // ---- pass 1: where does real data end? ---------------------------------------------------------------

    public static SheetScan Scan(Stream stream)
    {
        var scan = new SheetScan();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false });
        var sheetDataDepth = -1;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || !SpreadsheetNamespaces.IsMain(reader.NamespaceURI))
            {
                continue;
            }

            if (reader.LocalName == "sheetData")
            {
                sheetDataDepth = reader.Depth;
                continue;
            }

            switch (reader.LocalName)
            {
                case "c" when sheetDataDepth >= 0 && reader.Depth == sheetDataDepth + 2:
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
                case "autoFilter":
                    // A range can be written from either corner (A20:C1); the lower row decides.
                    scan.LastMergedRow = Math.Max(scan.LastMergedRow, LastRowOf(reader.GetAttribute("ref")));
                    break;
            }
        }

        return scan;
    }

    // ---- pass 2/3: rewrite ------------------------------------------------------------------------------

    protected override bool OnElement(XmlReader reader, XmlWriter writer)
    {
        // Other vocabularies reuse these names (drawing anchors have <xdr:row>40</xdr:row>); only spreadsheetml counts.
        if (!SpreadsheetNamespaces.IsMain(reader.NamespaceURI))
        {
            return false;
        }

        switch (reader.LocalName)
        {
            case "sheetData":
                _sheetDataDepth = reader.Depth;
                return false;

            case "col":
                RecordColumnStyle(reader);
                return false;

            case "sheetFormatPr":
                if (double.TryParse(reader.GetAttribute("defaultRowHeight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var defaultHeight) && defaultHeight > 0)
                {
                    _defaultRowHeight = defaultHeight;
                }

                return false;

            case "dimension" when _deferDimension && _sheetDataDepth < 0:
                OriginalDimension = reader.GetAttribute("ref");
                _placeholderWritten = true;
                writer.WriteStartElement(reader.Prefix, "dimension", reader.NamespaceURI);
                writer.WriteAttributeString("ref", DimensionPlaceholder);
                writer.WriteEndElement();
                SkipElement(reader);
                return true;

            case "row" when IsSheetRow(reader) && !reader.IsEmptyElement:
                RewriteRow(reader, writer);
                return true;

            case "row" when IsSheetRow(reader):
                RewriteEmptyRow(reader, writer);
                return true;
        }

        return false;
    }

    private bool IsSheetRow(XmlReader reader) => _sheetDataDepth >= 0 && reader.Depth == _sheetDataDepth + 1;

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
        _ = int.TryParse(Find(attributes, "r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var emptyRowNumber);
        if (!RowAttributesMatter(attributes, _aggressive && emptyRowNumber > _cutoffRow))
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
            if (row.NodeType == XmlNodeType.Element && row.LocalName == "c" && row.Depth == 1 && SpreadsheetNamespaces.IsMain(row.NamespaceURI))
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
        else if (RowAttributesMatter(attributes, pastData))
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

    /// <summary>
    /// Whether an otherwise empty row still says something. A row-level format (number format, font, fill) is what a cell typed
    /// into that row will look like, so any non-default row style is kept - even one that draws nothing today - and so are hidden,
    /// grouped and resized rows. What goes is a row that only restates defaults (<c>s="0"</c>, the sheet's own height). Below
    /// all data, aggressive mode drops the rest too: row styles and custom heights, as it already does for formatted blank cells.
    /// This is what clears the million <c>&lt;row r="500000" s="2" customFormat="1"/&gt;</c> left by a "format the whole sheet".
    /// </summary>
    private bool RowAttributesMatter(List<(string Prefix, string Local, string Ns, string Value)> attributes, bool pastData)
    {
        var customFormat = IsTrue(Find(attributes, "customFormat"));
        var styleCarries = !pastData && customFormat
                           && int.TryParse(Find(attributes, "s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowStyle)
                           && rowStyle != 0;
        var heightMatters = !pastData && IsTrue(Find(attributes, "customHeight"))
                            && double.TryParse(Find(attributes, "ht"), NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                            && Math.Abs(height - _defaultRowHeight) > 0.01;

        foreach (var (_, local, _, _) in attributes)
        {
            switch (local)
            {
                case "r" or "spans" or "dyDescent" or "xmlns":
                    break;
                // A style index only applies with customFormat; ht without customHeight is a cached auto height.
                case "s" or "customFormat" or "ht" or "customHeight":
                    break;
                default:
                    // hidden, outlineLevel, collapsed, thickTop, thickBot, ph: behaviour or layout that must stay.
                    return true;
            }
        }

        return styleCarries || heightMatters;
    }

    /// <summary>The highest row any corner of a range ("A1:C20", "C20:A1", "B7") refers to; 0 when there is none to read.</summary>
    private static int LastRowOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return 0;
        }

        var highest = 0;
        foreach (var range in reference.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var corner in range.Split(':'))
            {
                if (TryParseCellRef(corner, out _, out var row))
                {
                    highest = Math.Max(highest, row);
                }
            }
        }

        return highest;
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
