using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ClopWindows.Core.Shared;

public static class StringExtensions
{
    private static readonly Regex SafeFilenameRegex = new("[\\\\/:?{}<>*|$#&^;'\"`\x00-\x09\x0B-\x0C\x0E-\x1F\n\t]", RegexOptions.Compiled);

    public static string SafeFilename(this string value) => SafeFilenameRegex.Replace(value, "_");

    public static FilePath? ToFilePath(this string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            return null;
        }
        var expanded = ExpandUserDirectory(value.TrimmedPath());
        return FilePath.From(expanded);
    }

    public static string TrimmedPath(this string value) => value.Trim('\"', '\'', '\n', '\t', ' ', '{', '}', ',');

    public static string ReplaceFirst(this string value, string target, string replacement)
    {
        var index = value.IndexOf(target, StringComparison.Ordinal);
        if (index < 0)
        {
            return value;
        }
        return string.Concat(value.AsSpan(0, index), replacement, value.AsSpan(index + target.Length));
    }

    public static string ShellString(this string value)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return value.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
    }

    public static string ShellString(this FilePath path) => path.Value.ShellString();

    private static string ExpandUserDirectory(string path)
    {
        if (path.StartsWith("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var remainder = path.Length > 1 && (path[1] == '/' || path[1] == '\\') ? path[1..] : path;
            return Path.Combine(home, remainder.TrimStart('/', '\\'));
        }
        return Environment.ExpandEnvironmentVariables(path);
    }
}
