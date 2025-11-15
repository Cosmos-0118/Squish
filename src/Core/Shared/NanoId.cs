using System;
using System.Security.Cryptography;
using System.Text;

namespace ClopWindows.Core.Shared;

public sealed class NanoId
{
    private readonly string _alphabet;
    private readonly int _size;

    private static readonly string DefaultAlphabet = NanoIdAlphabet.UrlSafe.ToAlphabet();
    private const int DefaultSize = 21;

    public NanoId(NanoIdAlphabet alphabet, int size)
    {
        _alphabet = NanoIdAlphabetHelper.Parse(alphabet);
        _size = size;
    }

    public string Create() => Generate(_alphabet, _size);

    public static string New() => Generate(DefaultAlphabet, DefaultSize);

    public static string New(NanoIdAlphabet alphabet, int size) => Generate(NanoIdAlphabetHelper.Parse(alphabet), size);

    public static string New(params NanoIdAlphabet[] alphabets) => Generate(NanoIdAlphabetHelper.Parse(alphabets), DefaultSize);

    public static string New(int size) => Generate(DefaultAlphabet, size);

    public static string Random(int maxSize = 40)
    {
        if (maxSize < 10)
        {
            return New(NanoIdAlphabet.All, maxSize);
        }
        var randomSize = RandomNumberGenerator.GetInt32(10, Math.Max(11, maxSize + 1));
        return Generate(NanoIdAlphabet.All.ToAlphabet(), randomSize);
    }

    private static string Generate(string alphabet, int size)
    {
        if (string.IsNullOrWhiteSpace(alphabet))
        {
            throw new ArgumentException("Alphabet cannot be empty", nameof(alphabet));
        }
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
        var buffer = new char[size];
        for (var i = 0; i < size; i++)
        {
            var index = RandomNumberGenerator.GetInt32(alphabet.Length);
            buffer[i] = alphabet[index];
        }
        return new string(buffer);
    }
}

public enum NanoIdAlphabet
{
    UrlSafe,
    UppercasedLatinLetters,
    LowercasedLatinLetters,
    Numbers,
    Symbols,
    All
}

internal static class NanoIdAlphabetHelper
{
    public static string Parse(params NanoIdAlphabet[] alphabets)
    {
        var builder = new StringBuilder();
        foreach (var alphabet in alphabets)
        {
            builder.Append(alphabet.ToAlphabet());
        }
        return builder.ToString();
    }

    public static string ToAlphabet(this NanoIdAlphabet alphabet) => alphabet switch
    {
        NanoIdAlphabet.UppercasedLatinLetters => "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        NanoIdAlphabet.LowercasedLatinLetters => "abcdefghijklmnopqrstuvwxyz",
        NanoIdAlphabet.Numbers => "0123456789",
        NanoIdAlphabet.Symbols => "§±!@#$%^&*()_+-=[]{};':,.<>?`~ /|",
        NanoIdAlphabet.UrlSafe => Parse(NanoIdAlphabet.UppercasedLatinLetters, NanoIdAlphabet.LowercasedLatinLetters, NanoIdAlphabet.Numbers) + "~_",
        NanoIdAlphabet.All => Parse(NanoIdAlphabet.UppercasedLatinLetters, NanoIdAlphabet.LowercasedLatinLetters, NanoIdAlphabet.Numbers, NanoIdAlphabet.Symbols),
        _ => string.Empty
    };
}
