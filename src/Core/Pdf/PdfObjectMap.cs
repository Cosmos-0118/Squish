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

    /// <summary>
    /// The one filter left on a stream. /Filter may be a name or an array of names (ISO 32000-1 table 5); a single-element array
    /// means the same as the bare name. Returns null for no filter or a real filter chain.
    /// </summary>
    public static string? SingleFilter(JsonElement dict)
    {
        if (dict.ValueKind != JsonValueKind.Object || !dict.TryGetProperty("/Filter", out var filter))
        {
            return null;
        }

        return filter.ValueKind switch
        {
            JsonValueKind.String => filter.GetString(),
            JsonValueKind.Array when filter.GetArrayLength() == 1 && filter[0].ValueKind == JsonValueKind.String => filter[0].GetString(),
            _ => null
        };
    }

    public static bool HasFilter(JsonElement dict)
        => dict.ValueKind == JsonValueKind.Object && dict.TryGetProperty("/Filter", out var f)
           && !(f.ValueKind == JsonValueKind.Array && f.GetArrayLength() == 0) && f.ValueKind != JsonValueKind.Null;

    public static double? Number(JsonElement dict, string key)
        => dict.ValueKind == JsonValueKind.Object && dict.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;

    /// <summary>
    /// Objects reachable from the trailer. A file can carry objects nothing points to any more (earlier revisions, deleted
    /// pages); qpdf drops them on write, so there is no point in optimising their pictures.
    /// </summary>
    public HashSet<int> ReachableIds()
    {
        var seen = new HashSet<int>();
        var pending = new Stack<JsonElement>();
        if (Trailer is { } trailer)
        {
            pending.Push(trailer);
        }

        while (pending.Count > 0)
        {
            var element = pending.Pop();
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    if (TryGetRef(element, out var id) && _objects.TryGetValue(id, out var target) && seen.Add(id))
                    {
                        pending.Push(target);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        pending.Push(property.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        pending.Push(item);
                    }

                    break;
            }
        }

        return seen;
    }

    /// <summary>Whether any stream declares a PNG or TIFF predictor (/Predictor above 1) in its decode parameters.</summary>
    public bool HasPredictorStreams()
    {
        foreach (var o in _objects.Values)
        {
            if (o.TryGetProperty("stream", out var stream)
                && stream.TryGetProperty("dict", out var dict)
                && dict.TryGetProperty("/DecodeParms", out var parms)
                && DeclaresPredictor(parms))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DeclaresPredictor(JsonElement parms)
    {
        switch (parms.ValueKind)
        {
            case JsonValueKind.Object:
                return Int(parms, "/Predictor") is > 1;
            case JsonValueKind.Array:
                foreach (var item in parms.EnumerateArray())
                {
                    if (DeclaresPredictor(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    public static int? Int(JsonElement dict, string key)
        => dict.ValueKind == JsonValueKind.Object && dict.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    [GeneratedRegex(@"^(\d+) 0 R$")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^obj:(\d+) 0 R$")]
    private static partial Regex ObjectKeyPattern();
}
