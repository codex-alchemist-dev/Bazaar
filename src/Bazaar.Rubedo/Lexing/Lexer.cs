using System.Globalization;

namespace Bazaar.Rubedo.Lexing;

/// <summary>Turns source text into tokens. Throws <see cref="RubedoException"/> on a bad character or a bad literal.</summary>
internal static class Lexer
{
    private static readonly HashSet<string> _keywords = new(StringComparer.Ordinal) { "true", "false" };

    private static readonly string[] _twoCharOperators = ["==", "!=", "<=", ">=", "&&", "||"];

    private static readonly HashSet<char> _oneCharOperators = ['<', '>', '!', '+', '-', '*', '/', '%'];

    public static List<Token> Tokenize(string source, RubedoOptions options)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (tokens.Count >= options.MaxTokens)
            {
                throw new RubedoException("too-many-tokens", $"An expression may have at most {options.MaxTokens} tokens.", i);
            }

            int start = i;
            if (IsAsciiDigit(c))
            {
                while (i < source.Length && IsAsciiDigit(source[i])) i++;
                if (i < source.Length && source[i] == '.')
                {
                    i++;
                    if (i >= source.Length || !IsAsciiDigit(source[i]))
                    {
                        throw new RubedoException("bad-number", "A decimal point needs digits after it.", start);
                    }

                    while (i < source.Length && IsAsciiDigit(source[i])) i++;
                }

                tokens.Add(new Token(TokenKind.Number, source[start..i], start));
                continue;
            }

            if (IsAsciiLetter(c))
            {
                while (i < source.Length && (IsAsciiLetter(source[i]) || IsAsciiDigit(source[i]))) i++;
                string word = source[start..i];
                tokens.Add(new Token(_keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier, word, start));
                continue;
            }

            if (c == '"')
            {
                i++;
                var content = new System.Text.StringBuilder();
                while (true)
                {
                    if (i >= source.Length)
                    {
                        throw new RubedoException("unterminated-string", "A string literal is not closed.", start);
                    }

                    if (source[i] == '"')
                    {
                        i++;
                        break;
                    }

                    if (source[i] is '\\' or '\n' or '\r')
                    {
                        throw new RubedoException("bad-string", "Strings have no escapes and no line breaks in Rubedo v1.", i);
                    }

                    content.Append(source[i]);
                    if (content.Length > options.MaxStringChars)
                    {
                        throw new RubedoException("string-too-long", $"A string literal may have at most {options.MaxStringChars} characters.", start);
                    }

                    i++;
                }

                tokens.Add(new Token(TokenKind.String, content.ToString(), start));
                continue;
            }

            if (i + 1 < source.Length)
            {
                string two = source.Substring(i, 2);
                if (Array.IndexOf(_twoCharOperators, two) >= 0)
                {
                    tokens.Add(new Token(TokenKind.Operator, two, start));
                    i += 2;
                    continue;
                }
            }

            if (_oneCharOperators.Contains(c))
            {
                tokens.Add(new Token(TokenKind.Operator, c.ToString(), start));
                i++;
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.LeftParen, "(", start));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RightParen, ")", start));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", start));
                    i++;
                    continue;
            }

            throw new RubedoException("bad-character", $"The character '{c}' is not allowed.", i);
        }

        if (tokens.Count >= options.MaxTokens)
        {
            throw new RubedoException("too-many-tokens", $"An expression may have at most {options.MaxTokens} tokens.", source.Length);
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, source.Length));
        return tokens;
    }

    /// <summary>Parses a numeric literal exactly, so 0.1 stays 0.1 (decimal, invariant culture).</summary>
    public static bool TryParseNumber(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static bool IsAsciiLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}
