using System.Globalization;
using System.Text;

namespace Bazaar.Localisation;

/// <summary>
/// A formatter for the subset of ICU MessageFormat that doc 49 section 2 asks for: <c>{name}</c> arguments,
/// <c>{n, plural, one {# file} other {# files}}</c> (with <c>=N</c> exact matches, <c>offset:N</c> and <c>#</c>) and
/// <c>{who, select, female {...} other {...}}</c> for gender. Apostrophes follow ICU's default: <c>''</c> is one
/// apostrophe, and <c>'</c> before a brace starts quoted text. Pure. A malformed template throws
/// <see cref="FormatException"/>; <see cref="LocalisedText.ResolveIcu"/> turns that into a visible placeholder.
/// </summary>
public static class IcuMessage
{
    /// <summary>Formats <paramref name="template"/> for <paramref name="locale"/> with named arguments.</summary>
    public static string Format(string template, string locale, IReadOnlyDictionary<string, object?> args)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(args);

        var parser = new Parser(template, locale, args);
        return parser.ParseMessage(inPlural: false, pluralValue: 0, untilCloseBrace: false);
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly string _locale;
        private readonly IReadOnlyDictionary<string, object?> _args;
        private readonly CultureInfo _culture;
        private int _i;

        public Parser(string s, string locale, IReadOnlyDictionary<string, object?> args)
        {
            _s = s;
            _locale = locale;
            _args = args;
            try
            {
                _culture = CultureInfo.GetCultureInfo(locale);
            }
            catch (CultureNotFoundException)
            {
                _culture = CultureInfo.InvariantCulture;
            }
        }

        // Reads text and arguments until the end, or until the '}' that closes the enclosing branch.
        public string ParseMessage(bool inPlural, decimal pluralValue, bool untilCloseBrace)
        {
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '}')
                {
                    if (untilCloseBrace)
                    {
                        return sb.ToString();
                    }

                    throw new FormatException($"Unmatched '}}' at {_i}.");
                }

                if (c == '{')
                {
                    _i++;
                    sb.Append(ParseArgument());
                    continue;
                }

                if (c == '\'')
                {
                    ReadApostrophe(sb, inPlural);
                    continue;
                }

                if (c == '#' && inPlural)
                {
                    sb.Append(pluralValue.ToString("N0", _culture));
                    _i++;
                    continue;
                }

                sb.Append(c);
                _i++;
            }

            if (untilCloseBrace)
            {
                throw new FormatException("A '{' was never closed.");
            }

            return sb.ToString();
        }

        private void ReadApostrophe(StringBuilder sb, bool inPlural)
        {
            char? next = _i + 1 < _s.Length ? _s[_i + 1] : null;
            if (next == '\'')
            {
                sb.Append('\'');
                _i += 2;
                return;
            }

            bool quoteStart = next is '{' or '}' || (inPlural && next == '#');
            if (!quoteStart)
            {
                sb.Append('\'');
                _i++;
                return;
            }

            _i++;
            while (_i < _s.Length)
            {
                if (_s[_i] == '\'')
                {
                    if (_i + 1 < _s.Length && _s[_i + 1] == '\'')
                    {
                        sb.Append('\'');
                        _i += 2;
                        continue;
                    }

                    _i++;
                    return;
                }

                sb.Append(_s[_i]);
                _i++;
            }
        }

        private string ParseArgument()
        {
            string name = ReadToken();
            if (name.Length == 0)
            {
                throw new FormatException($"An argument has no name at {_i}.");
            }

            SkipSpaces();
            if (Peek() == '}')
            {
                _i++;
                return Display(Lookup(name));
            }

            Expect(',');
            string type = ReadToken();
            SkipSpaces();
            switch (type)
            {
                case "number":
                    ExpectCloseAfterOptionalComma();
                    return Display(Lookup(name));
                case "plural":
                    Expect(',');
                    return ParsePlural(name);
                case "select":
                    Expect(',');
                    return ParseSelect(name);
                default:
                    throw new FormatException($"Unknown argument type '{type}'.");
            }
        }

        private void ExpectCloseAfterOptionalComma()
        {
            SkipSpaces();
            Expect('}');
        }

        private string ParsePlural(string name)
        {
            object? raw = Lookup(name);
            decimal value = ToNumber(raw, name);
            decimal offset = 0;
            SkipSpaces();
            if (string.CompareOrdinal(_s, _i, "offset:", 0, 7) == 0)
            {
                _i += 7;
                offset = decimal.Parse(ReadToken(), CultureInfo.InvariantCulture);
            }

            decimal shown = value - offset;
            string category = PluralRules.Select(_locale, shown).ToString().ToLowerInvariant();
            string? chosen = null;
            string? other = null;
            while (true)
            {
                SkipSpaces();
                if (Peek() == '}')
                {
                    _i++;
                    break;
                }

                string selector = ReadToken();
                if (selector.Length == 0)
                {
                    throw new FormatException($"Expected a plural selector at {_i}.");
                }

                SkipSpaces();
                Expect('{');
                string text = ParseMessage(inPlural: true, pluralValue: shown, untilCloseBrace: true);
                Expect('}');

                if (selector.StartsWith('=') && decimal.TryParse(selector.AsSpan(1), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal exact))
                {
                    if (exact == value)
                    {
                        chosen = text;
                    }
                }
                else if (selector == category && chosen is null)
                {
                    chosen = text;
                }

                if (selector == "other")
                {
                    other = text;
                }
            }

            if (other is null)
            {
                throw new FormatException($"Plural '{name}' has no 'other' branch.");
            }

            return chosen ?? other;
        }

        private string ParseSelect(string name)
        {
            string key = Display(Lookup(name));
            string? chosen = null;
            string? other = null;
            while (true)
            {
                SkipSpaces();
                if (Peek() == '}')
                {
                    _i++;
                    break;
                }

                string selector = ReadToken();
                if (selector.Length == 0)
                {
                    throw new FormatException($"Expected a select key at {_i}.");
                }

                SkipSpaces();
                Expect('{');
                string text = ParseMessage(inPlural: false, pluralValue: 0, untilCloseBrace: true);
                Expect('}');

                if (selector == key && chosen is null)
                {
                    chosen = text;
                }

                if (selector == "other")
                {
                    other = text;
                }
            }

            if (other is null)
            {
                throw new FormatException($"Select '{name}' has no 'other' branch.");
            }

            return chosen ?? other;
        }

        private object? Lookup(string name) =>
            _args.TryGetValue(name, out object? v) ? v : throw new FormatException($"No argument named '{name}'.");

        private string Display(object? v) => v switch
        {
            null => string.Empty,
            IFormattable f => f.ToString(null, _culture),
            _ => v.ToString() ?? string.Empty,
        };

        private static decimal ToNumber(object? v, string name) => v switch
        {
            decimal d => d,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            double dbl => (decimal)dbl,
            float fl => (decimal)fl,
            _ => throw new FormatException($"Argument '{name}' is not a number."),
        };

        private char? Peek() => _i < _s.Length ? _s[_i] : null;

        private void SkipSpaces()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
        }

        private void Expect(char c)
        {
            SkipSpaces();
            if (Peek() != c)
            {
                throw new FormatException($"Expected '{c}' at {_i}.");
            }

            _i++;
        }

        private string ReadToken()
        {
            SkipSpaces();
            int start = _i;
            while (_i < _s.Length && !char.IsWhiteSpace(_s[_i]) && _s[_i] is not (',' or '{' or '}'))
            {
                _i++;
            }

            return _s[start.._i];
        }
    }
}
