using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ClopWindows.App.Infrastructure;

internal static class ShortcutParser
{
    private static readonly KeyGestureConverter GestureConverter = new();
    private static readonly KeyConverter KeyConverter = new();

    public static bool TryParse(string? value, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        try
        {
            if (GestureConverter.ConvertFromInvariantString(text) is KeyGesture gesture && gesture.Key != Key.None)
            {
                modifiers = gesture.Modifiers;
                key = gesture.Key;
                return true;
            }
        }
        catch
        {
            // fall back to manual parse
        }

        return TryParseManually(text, out modifiers, out key);
    }

    public static string ToStorageString(ModifierKeys modifiers, Key key)
    {
        if (key == Key.None)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyToStorageToken(key));
        return string.Join('+', parts);
    }

    public static string ToDisplayString(ModifierKeys modifiers, Key key)
    {
        if (key == Key.None)
        {
            return "Disabled";
        }

        return string.Join("+", EnumerateModifierLabels(modifiers).Append(KeyToDisplayToken(key)));
    }

    private static bool TryParseManually(string value, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        var tokens = value
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (tokens.Length == 0)
        {
            return false;
        }

        var index = 0;
        for (; index < tokens.Length - 1; index++)
        {
            var token = tokens[index];
            if (!TryParseModifier(token, ref modifiers))
            {
                break;
            }
        }

        var keyToken = string.Join('+', tokens.Skip(index));
        if (IsModifierToken(keyToken))
        {
            return false;
        }

        if (!TryParseKeyToken(keyToken, out key))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseModifier(string token, ref ModifierKeys modifiers)
    {
        if (IsModifierToken(token))
        {
            modifiers |= token.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "cmd" or "command" => ModifierKeys.Control,
                "shift" => ModifierKeys.Shift,
                "alt" or "option" => ModifierKeys.Alt,
                "win" or "windows" or "super" => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
            return true;
        }

        return false;
    }

    private static bool TryParseKeyToken(string token, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.Length == 1)
        {
            var ch = token[0];
            if (char.IsDigit(ch))
            {
                key = Key.D0 + (ch - '0');
                return true;
            }
            if (char.IsLetter(ch))
            {
                key = Key.A + (char.ToUpperInvariant(ch) - 'A');
                return true;
            }
            if (SpecialKeyMap.TryGetValue(token, out key))
            {
                return true;
            }
        }

        if (SpecialKeyMap.TryGetValue(token, out key))
        {
            return true;
        }

        try
        {
            if (KeyConverter.ConvertFromInvariantString(token) is Key converted && converted != Key.None)
            {
                key = converted;
                return true;
            }
        }
        catch
        {
            if (Enum.TryParse(token, true, out Key parsed) && parsed != Key.None)
            {
                key = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool IsModifierToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        token = token.ToLowerInvariant();
        return token is "ctrl" or "control" or "cmd" or "command" or "shift" or "alt" or "option" or "win" or "windows" or "super";
    }

    private static IEnumerable<string> EnumerateModifierLabels(ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            yield return "Ctrl";
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            yield return "Shift";
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            yield return "Alt";
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            yield return "Win";
        }
    }

    private static string KeyToStorageToken(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            return ((char)('A' + (key - Key.A))).ToString();
        }

        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (SpecialTokenByKey.TryGetValue(key, out var text))
        {
            return text;
        }

        var converted = KeyConverter.ConvertToInvariantString(key);
        return string.IsNullOrWhiteSpace(converted) ? key.ToString() : converted;
    }

    private static string KeyToDisplayToken(Key key)
    {
        if (DisplayTokenByKey.TryGetValue(key, out var token))
        {
            return token;
        }

        return KeyToStorageToken(key);
    }

    private static readonly (string Token, Key Key, string[] Synonyms)[] SpecialKeyMetadata =
    {
        ("OemComma", Key.OemComma, new[] {",", "Comma"}),
        ("OemPeriod", Key.OemPeriod, new[] {".", "Period"}),
        ("OemSemicolon", Key.OemSemicolon, new[] {";", "Semicolon"}),
        ("OemQuotes", Key.OemQuotes, new[] {"'", "Quote"}),
        ("OemQuestion", Key.OemQuestion, new[] {"/", "Question"}),
        ("OemBackslash", Key.OemBackslash, new[] {"\\", "Backslash"}),
        ("OemMinus", Key.OemMinus, new[] {"-", "Minus"}),
        ("OemPlus", Key.OemPlus, new[] {"=", "Plus"}),
        ("Space", Key.Space, new[] {"Spacebar"}),
        ("Tab", Key.Tab, Array.Empty<string>()),
        ("Enter", Key.Enter, new[] {"Return"}),
        ("Back", Key.Back, new[] {"Backspace"}),
        ("Delete", Key.Delete, Array.Empty<string>()),
        ("Insert", Key.Insert, Array.Empty<string>()),
        ("Home", Key.Home, Array.Empty<string>()),
        ("End", Key.End, Array.Empty<string>()),
        ("PageUp", Key.PageUp, Array.Empty<string>()),
        ("PageDown", Key.PageDown, Array.Empty<string>()),
        ("Up", Key.Up, Array.Empty<string>()),
        ("Down", Key.Down, Array.Empty<string>()),
        ("Left", Key.Left, Array.Empty<string>()),
        ("Right", Key.Right, Array.Empty<string>())
    };

    private static readonly Dictionary<string, Key> SpecialKeyMap = BuildSpecialKeyMap();
    private static readonly Dictionary<Key, string> SpecialTokenByKey = SpecialKeyMetadata
        .ToDictionary(item => item.Key, item => item.Token, EqualityComparer<Key>.Default);
    private static readonly Dictionary<Key, string> DisplayTokenByKey = new()
    {
        { Key.OemComma, "," },
        { Key.OemPeriod, "." },
        { Key.OemSemicolon, ";" },
        { Key.OemQuotes, "'" },
        { Key.OemQuestion, "/" },
        { Key.OemBackslash, "\\" },
        { Key.OemMinus, "-" },
        { Key.OemPlus, "=" },
        { Key.Space, "Space" }
    };

    private static Dictionary<string, Key> BuildSpecialKeyMap()
    {
        var dictionary = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in SpecialKeyMetadata)
        {
            dictionary[item.Token] = item.Key;
            foreach (var synonym in item.Synonyms)
            {
                dictionary[synonym] = item.Key;
            }
        }

        return dictionary;
    }
}
