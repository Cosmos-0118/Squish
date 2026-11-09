using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Squish.Core.Pdf;

/// <summary>Affine transform in PDF row-vector form: [a b c d e f].</summary>
internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>this x other: apply this first, then other.</summary>
    public PdfMatrix Then(PdfMatrix other) => new(
        A * other.A + B * other.C,
        A * other.B + B * other.D,
        C * other.A + D * other.C,
        C * other.B + D * other.D,
        E * other.A + F * other.C + other.E,
        E * other.B + F * other.D + other.F);

    /// <summary>Size in points that the unit square occupies after this transform (handles rotation and skew).</summary>
    public (double Width, double Height) UnitSquareSize => (Math.Sqrt(A * A + B * B), Math.Sqrt(C * C + D * D));
}

/// <summary>
/// Just enough of a content-stream interpreter to learn where XObjects are painted and how big: the graphics-state stack
/// (<c>q</c>/<c>Q</c>), <c>cm</c>, and <c>Do</c>. Everything else is skipped. Inline images are stepped over.
/// </summary>
internal static class PdfContentScanner
{
    private const int MaxOperands = 8;

    /// <exception cref="FormatException">The stream is not something this scanner understands; callers must treat all uses as unknown.</exception>
    public static void Scan(ReadOnlySpan<byte> data, PdfMatrix start, Action<string, PdfMatrix> onDo)
    {
        var stack = new Stack<PdfMatrix>();
        var ctm = start;
        var operands = new List<object>(MaxOperands);
        var position = 0;

        while (NextToken(data, ref position, out var token))
        {
            switch (token.Kind)
            {
                case TokenKind.Number:
                    operands.Add(token.Number);
                    break;
                case TokenKind.Name:
                    operands.Add(token.Text);
                    break;
                case TokenKind.Other:
                    // strings, arrays, dictionaries: operands we do not need, but they must not be mistaken for operators
                    operands.Add(string.Empty);
                    break;
                case TokenKind.Operator:
                    switch (token.Text)
                    {
                        case "q":
                            stack.Push(ctm);
                            break;
                        case "Q":
                            if (stack.Count > 0)
                            {
                                ctm = stack.Pop();
                            }

                            break;
                        case "cm":
                            if (TryTakeNumbers(operands, 6, out var m))
                            {
                                ctm = new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]).Then(ctm);
                            }

                            break;
                        case "Do":
                            if (operands.Count > 0 && operands[^1] is string name && name.Length > 0)
                            {
                                onDo(name, ctm);
                            }

                            break;
                        case "BI":
                            SkipInlineImage(data, ref position);
                            break;
                    }

                    operands.Clear();
                    break;
            }

            if (operands.Count > MaxOperands * 4)
            {
                operands.RemoveRange(0, operands.Count - MaxOperands);
            }
        }
    }

    private static bool TryTakeNumbers(List<object> operands, int count, out double[] values)
    {
        values = new double[count];
        if (operands.Count < count)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (operands[operands.Count - count + i] is not double number)
            {
                return false;
            }

            values[i] = number;
        }

        return true;
    }

    private enum TokenKind { Number, Name, Operator, Other }

    private readonly record struct Token(TokenKind Kind, string Text, double Number);

    private static bool IsWhite(byte b) => b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;

    private static bool IsDelimiter(byte b) => b == '(' || b == ')' || b == '<' || b == '>' || b == '[' || b == ']' || b == '{' || b == '}' || b == '/' || b == '%';

    private static bool NextToken(ReadOnlySpan<byte> data, ref int position, out Token token)
    {
        token = default;
        while (position < data.Length)
        {
            var b = data[position];
            if (IsWhite(b))
            {
                position++;
            }
            else if (b == '%')
            {
                while (position < data.Length && data[position] != 10 && data[position] != 13)
                {
                    position++;
                }
            }
            else
            {
                break;
            }
        }

        if (position >= data.Length)
        {
            return false;
        }

        var c = data[position];
        if (c == '/')
        {
            var start = ++position;
            while (position < data.Length && !IsWhite(data[position]) && !IsDelimiter(data[position]))
            {
                position++;
            }

            token = new Token(TokenKind.Name, Encoding.Latin1.GetString(data[start..position]), 0);
            return true;
        }

        if (c == '(')
        {
            SkipLiteralString(data, ref position);
            token = new Token(TokenKind.Other, string.Empty, 0);
            return true;
        }

        if (c == '<')
        {
            if (position + 1 < data.Length && data[position + 1] == '<')
            {
                position += 2;
            }
            else
            {
                while (position < data.Length && data[position] != '>')
                {
                    position++;
                }

                position++;
            }

            token = new Token(TokenKind.Other, string.Empty, 0);
            return true;
        }

        if (c == '>')
        {
            position += position + 1 < data.Length && data[position + 1] == '>' ? 2 : 1;
            token = new Token(TokenKind.Other, string.Empty, 0);
            return true;
        }

        if (c == '[' || c == ']' || c == '{' || c == '}' || c == ')')
        {
            position++;
            token = new Token(TokenKind.Other, string.Empty, 0);
            return true;
        }

        var begin = position;
        while (position < data.Length && !IsWhite(data[position]) && !IsDelimiter(data[position]))
        {
            position++;
        }

        var text = Encoding.Latin1.GetString(data[begin..position]);
        token = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? new Token(TokenKind.Number, text, number)
            : new Token(TokenKind.Operator, text, 0);
        return true;
    }

    private static void SkipLiteralString(ReadOnlySpan<byte> data, ref int position)
    {
        var depth = 0;
        while (position < data.Length)
        {
            var b = data[position++];
            if (b == '\\')
            {
                position++;
            }
            else if (b == '(')
            {
                depth++;
            }
            else if (b == ')' && --depth == 0)
            {
                return;
            }
        }

        throw new FormatException("Unterminated string in content stream.");
    }

    /// <summary>Positioned just after <c>BI</c>: skips the key/value dictionary, <c>ID</c>, the binary data and <c>EI</c>.</summary>
    private static void SkipInlineImage(ReadOnlySpan<byte> data, ref int position)
    {
        var id = IndexOfKeyword(data, position, "ID");
        if (id < 0)
        {
            throw new FormatException("Inline image without ID.");
        }

        var search = id + 3;
        while (true)
        {
            var ei = IndexOfKeyword(data, search, "EI");
            if (ei < 0)
            {
                throw new FormatException("Inline image without EI.");
            }

            // Binary data can contain the bytes "EI"; require that what follows looks like content-stream text again.
            if (LooksLikeContent(data, ei + 2))
            {
                position = ei + 2;
                return;
            }

            search = ei + 2;
        }
    }

    private static bool LooksLikeContent(ReadOnlySpan<byte> data, int from)
    {
        var end = Math.Min(data.Length, from + 12);
        for (var i = from; i < end; i++)
        {
            if (data[i] > 127 || (data[i] < 32 && !IsWhite(data[i])))
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOfKeyword(ReadOnlySpan<byte> data, int from, string keyword)
    {
        for (var i = Math.Max(0, from); i + keyword.Length <= data.Length; i++)
        {
            if (data[i] != keyword[0] || data[i + 1] != keyword[1])
            {
                continue;
            }

            var before = i == 0 || IsWhite(data[i - 1]);
            var after = i + keyword.Length >= data.Length || IsWhite(data[i + keyword.Length]);
            if (before && after)
            {
                return i;
            }
        }

        return -1;
    }
}
