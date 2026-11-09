using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Squish.Core.Pdf;

/// <summary>Read-only view of qpdf's JSON (v2) representation of a PDF: object number to value or stream dictionary.</summary>
internal sealed partial class PdfObjectMap : IDisposable
{
    private readonly JsonDocument _document;
    private readonly Dictionary<int, JsonElement> _objects = new();

    private PdfObjectMap(JsonDocument document)
    {
        _document = document;
    }

    public JsonElement Header { get; private set; }

    public JsonElement? Trailer { get; private set; }

    public IEnumerable<int> Ids => _objects.Keys;

    public static PdfObjectMap Load(string jsonPath)
    {
        var document = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
        var map = new PdfObjectMap(document);
        var root = document.RootElement.GetProperty("qpdf");
        map.Header = root[0];
        foreach (var property in root[1].EnumerateObject())
        {
            if (property.Name == "trailer")
            {
                map.Trailer = property.Value;
            }
            else if (TryParseObjectKey(property.Name, out var id))
            {
                map._objects[id] = property.Value;
            }
        }

        return map;
    }

    public void Dispose() => _document.Dispose();

    public bool Contains(int id) => _objects.ContainsKey(id);

    public bool IsStream(int id) => _objects.TryGetValue(id, out var o) && o.TryGetProperty("stream", out _);

    /// <summary>Stream dictionary for streams, the value for everything else.</summary>
    public JsonElement? ValueOf(int id)
    {
        if (!_objects.TryGetValue(id, out var o))
        {
            return null;
        }

        if (o.TryGetProperty("stream", out var stream))
        {
            return stream.TryGetProperty("dict", out var dict) ? dict : null;
        }

        return o.TryGetProperty("value", out var value) ? value : null;
    }

    public string? DataFile(int id)
        => _objects.TryGetValue(id, out var o) && o.TryGetProperty("stream", out var stream) && stream.TryGetProperty("datafile", out var file)
            ? file.GetString()
            : null;

    /// <summary>Follows an indirect reference; direct values come back as they are.</summary>
    public JsonElement? Deref(JsonElement element)
        => TryGetRef(element, out var id) ? ValueOf(id) : element;

    public static bool TryGetRef(JsonElement element, out int id)
    {
        id = 0;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (text is null)
        {
            return false;
        }

        var match = ReferencePattern().Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public static bool TryParseObjectKey(string key, out int id)
    {
        id = 0;
        var match = ObjectKeyPattern().Match(key);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public static string? Name(JsonElement dict, string key)
        => dict.ValueKind == JsonValueKind.Object && dict.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement dict, string key)
        => dict.ValueKind == JsonValueKind.Object && dict.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    [GeneratedRegex(@"^(\d+) 0 R$")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^obj:(\d+) 0 R$")]
    private static partial Regex ObjectKeyPattern();
}
