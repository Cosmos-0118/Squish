using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ClopWindows.Core.Shared;

public static class FilePathGenerator
{
    public static FilePath Generate(FilePath template, FilePath? source, ref int autoIncrementingNumber, bool createDirectories)
    {
        var resultPath = GenerateInternal(template.Value, source, ref autoIncrementingNumber);
        var generated = FilePath.From(resultPath);

        if (generated.IsRelative && source is { } basePath)
        {
            generated = basePath.Parent.Append(generated);
        }

        if (createDirectories)
        {
            var targetDirectory = string.IsNullOrEmpty(generated.Extension) ? generated : generated.Parent;
            targetDirectory.EnsurePathExists();
        }

        return generated;
    }

    public static FilePath? Generate(string template, FilePath? source, ref int autoIncrementingNumber, bool createDirectories)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        FilePath generated;
        try
        {
            generated = FilePath.From(GenerateInternal(template, source, ref autoIncrementingNumber));
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (generated.IsRelative && source is { } basePath)
        {
            generated = basePath.Parent.Append(generated);
        }

        if (createDirectories)
        {
            var targetDirectory = string.IsNullOrEmpty(generated.Extension) ? generated : generated.Parent;
            targetDirectory.EnsurePathExists();
        }

        return generated;
    }

    public static string GenerateFileName(string template, FilePath? source, ref int autoIncrementingNumber, bool safe = true)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }
        return ApplyTokens(template, source, ref autoIncrementingNumber, safe);
    }

    private static string GenerateInternal(string template, FilePath? source, ref int autoIncrementingNumber)
    {
        var root = Path.GetPathRoot(template) ?? string.Empty;
        var remainder = template.RootlessSubstring(root.Length);
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var components = remainder.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        var placeholders = autoIncrementingNumber;
        var generatedComponents = components
            .Select(component => ApplyTokens(component, source, ref placeholders, safe: !component.Contains(FileNameTokens.Path, StringComparison.Ordinal)))
            .ToArray();

        var result = CombineComponents(root, generatedComponents);
        if (result.Length == 0)
        {
            result = template;
        }

        autoIncrementingNumber = placeholders;
        return result;
    }

    private static string CombineComponents(string? root, IReadOnlyList<string>? components)
    {
        if ((components == null || components.Count == 0) && string.IsNullOrEmpty(root))
        {
            return string.Empty;
        }

        string? current = root;
        var items = components ?? Array.Empty<string>();
        foreach (var component in items)
        {
            current = string.IsNullOrEmpty(current) ? component : Path.Combine(current, component);
        }
        return current ?? root ?? string.Empty;
    }

    private static string ApplyTokens(string template, FilePath? source, ref int autoIncrementingNumber, bool safe)
    {
        var date = DateTime.Now;
        var calendar = CultureInfo.CurrentCulture.Calendar;
        var nextNumber = autoIncrementingNumber + 1;
        var usesAutoIncrement = template.Contains(FileNameTokens.AutoIncrementingNumber, StringComparison.Ordinal);

        var result = template
            .Replace(FileNameTokens.Year, date.ToString("yyyy", CultureInfo.InvariantCulture))
            .Replace(FileNameTokens.MonthNumeric, date.ToString("MM", CultureInfo.InvariantCulture))
            .Replace(FileNameTokens.MonthName, CultureInfo.CurrentCulture.DateTimeFormat.MonthNames[(date.Month - 1 + 12) % 12])
            .Replace(FileNameTokens.Day, date.ToString("dd", CultureInfo.InvariantCulture))
            .Replace(FileNameTokens.Weekday, ((int)calendar.GetDayOfWeek(date)).ToString(CultureInfo.InvariantCulture))
            .Replace(FileNameTokens.Hour, date.ToString("HH", CultureInfo.InvariantCulture))
            .Replace(FileNameTokens.Minutes, date.ToString("mm", CultureInfo.InvariantCulture))
            .Replace(FileNameTokens.Seconds, date.ToString("ss", CultureInfo.InvariantCulture))
            .Replace(FileNameTokens.AmPm, date.Hour >= 12 ? "PM" : "AM")
            .Replace(FileNameTokens.RandomCharacters, NanoId.New(NanoIdAlphabet.LowercasedLatinLetters, 5))
            .Replace(FileNameTokens.AutoIncrementingNumber, nextNumber.ToString(CultureInfo.InvariantCulture));

        if (usesAutoIncrement)
        {
            autoIncrementingNumber = nextNumber;
        }

        result = result
            .Replace(FileNameTokens.Path, source?.Parent.Value ?? string.Empty)
            .Replace(FileNameTokens.FileName, source?.Stem ?? string.Empty)
            .Replace(FileNameTokens.Extension, source?.Extension ?? string.Empty);

        if (safe)
        {
            result = result.SafeFilename();
            if (!string.IsNullOrWhiteSpace(source?.Extension))
            {
                var extension = source!.Value.Extension!;
                if (!result.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase))
                {
                    result = $"{result}.{extension}";
                }
            }
        }

        return result;
    }

    private static string RootlessSubstring(this string value, int startIndex)
    {
        if (startIndex <= 0 || startIndex >= value.Length)
        {
            return value;
        }
        return value[startIndex..];
    }
}

public static class FileNameTokens
{
    public const string Year = "%y";
    public const string MonthNumeric = "%m";
    public const string MonthName = "%n";
    public const string Day = "%d";
    public const string Weekday = "%w";
    public const string Hour = "%H";
    public const string Minutes = "%M";
    public const string Seconds = "%S";
    public const string AmPm = "%p";
    public const string RandomCharacters = "%r";
    public const string AutoIncrementingNumber = "%i";
    public const string FileName = "%f";
    public const string Extension = "%e";
    public const string Path = "%P";
}
