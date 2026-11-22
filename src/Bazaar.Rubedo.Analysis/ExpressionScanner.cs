namespace Bazaar.Rubedo.Analysis;

internal enum ScanKind
{
    Number,
    String,
    Identifier,
    Keyword,
    Operator,
    LeftParen,
    RightParen,
    Comma,
    Unknown,
}

/// <summary>A token with its source range. <see cref="Text"/> is the raw source slice, quotes included for a string.</summary>
internal readonly record struct ScanToken(ScanKind Kind, int Start, int End, string Text)
{
    public bool IsOperandEnd => Kind is ScanKind.Number or ScanKind.String or ScanKind.Identifier or ScanKind.Keyword or ScanKind.RightParen;

    /// <summary>True for a string with its closing quote.</summary>
    public bool IsTerminatedString => Kind == ScanKind.String && End - Start >= 2 && Text[^1] == '"';
}

/// <summary>
/// A forgiving scanner for editor use. Unlike the engine's lexer it never stops at the first problem, so completion, hover and
/// signature help work on half-typed source. It only finds token boundaries; the engine stays the one judge of validity.
/// </summary>
internal static class ExpressionScanner
{
    private static readonly string[] _twoCharOperators = ["==", "!=", "<=", ">=", "&&", "||"];

    public static List<ScanToken> Scan(string source)
    {
        var tokens = new List<ScanToken>();
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int start = i;
            if (char.IsAsciiDigit(c))
            {
                while (i < source.Length && char.IsAsciiDigit(source[i])) i++;
                if (i < source.Length && source[i] == '.')
                {
                    i++;
                    while (i < source.Length && char.IsAsciiDigit(source[i])) i++;
                }

                tokens.Add(new ScanToken(ScanKind.Number, start, i, source[start..i]));
            }
            else if (char.IsAsciiLetter(c))
            {
                while (i < source.Length && char.IsAsciiLetterOrDigit(source[i])) i++;
                string word = source[start..i];
                tokens.Add(new ScanToken(word is "true" or "false" ? ScanKind.Keyword : ScanKind.Identifier, start, i, word));
            }
            else if (c == '"')
            {
                i++;
                while (i < source.Length && source[i] is not ('"' or '\n' or '\r')) i++;
                if (i < source.Length && source[i] == '"')
                {
                    i++;
                }

                tokens.Add(new ScanToken(ScanKind.String, start, i, source[start..i]));
            }
            else if (i + 1 < source.Length && Array.IndexOf(_twoCharOperators, source.Substring(i, 2)) >= 0)
            {
                i += 2;
                tokens.Add(new ScanToken(ScanKind.Operator, start, i, source[start..i]));
            }
            else if (c is '<' or '>' or '!' or '+' or '-' or '*' or '/' or '%')
            {
                i++;
                tokens.Add(new ScanToken(ScanKind.Operator, start, i, source[start..i]));
            }
            else
            {
                i++;
                if (char.IsHighSurrogate(c) && i < source.Length && char.IsLowSurrogate(source[i]))
                {
                    i++;
                }

                ScanKind kind = c switch
                {
                    '(' => ScanKind.LeftParen,
                    ')' => ScanKind.RightParen,
                    ',' => ScanKind.Comma,
                    _ => ScanKind.Unknown,
                };
                tokens.Add(new ScanToken(kind, start, i, source[start..i]));
            }
        }

        return tokens;
    }
}
