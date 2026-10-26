using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ClopWindows.App.Localization;

public static class ClopStringCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CachedStrings = new(() => LoadCatalog(CultureInfo.CurrentUICulture));

    public static string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var strings = CachedStrings.Value;
        if (strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return key;
    }

    private static IReadOnlyDictionary<string, string> LoadCatalog(CultureInfo culture)
    {
        foreach (var candidate in EnumerateCandidates(culture))
        {
            if (TryLoadFromFile(candidate, out var fileStrings))
            {
                return fileStrings;
            }

            if (TryLoadFromEmbedded(candidate, out var embeddedStrings))
            {
                return embeddedStrings;
            }
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateCandidates(CultureInfo culture)
    {
        if (!string.IsNullOrWhiteSpace(culture.Name))
        {
            yield return culture.Name;
        }

        if (!string.IsNullOrWhiteSpace(culture.TwoLetterISOLanguageName))
        {
            yield return culture.TwoLetterISOLanguageName;
        }

        yield return "en";
    }

    private static bool TryLoadFromFile(string culture, out IReadOnlyDictionary<string, string> strings)
    {
        var candidateFiles = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Localization", $"{culture}.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Localization", $"{culture}.json")
        };

        foreach (var candidate in candidateFiles)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(candidate);
                strings = Deserialize(json);
                return true;
            }
            catch
            {
                // Ignore malformed files and fall through to the next candidate.
            }
        }

        strings = Array.Empty<KeyValuePair<string, string>>().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static bool TryLoadFromEmbedded(string culture, out IReadOnlyDictionary<string, string> strings)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNameCandidates = new[]
        {
            $"ClopWindows.App.Resources.strings.{culture}.json",
            $"ClopWindows.App.Resources.{culture}.json"
        };

        foreach (var resourceName in resourceNameCandidates)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            strings = Deserialize(json);
            return true;
        }

        strings = Array.Empty<KeyValuePair<string, string>>().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static IReadOnlyDictionary<string, string> Deserialize(string json)
    {
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(json, options);
        return dictionary is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase);
    }
}
