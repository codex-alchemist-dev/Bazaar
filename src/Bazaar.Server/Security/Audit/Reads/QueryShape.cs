using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Security.Audit.Reads;

/// <summary>
/// The shape of a SQL statement for read audit (doc 48 section 5.1). Literals, numbers and parameters become <c>?</c>,
/// comments are dropped and whitespace is collapsed, so two calls that differ only in values hash the same. The hash
/// identifies a query without storing its text, so the audit row holds no SQL and no values.
/// </summary>
public static class QueryShape
{
    private static readonly string[] _writeWords = ["insert", "update", "delete"];

    /// <summary>Lowercase hex SHA-256 of the normalised statement.</summary>
    public static string Hash(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(sql)));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// The statement kind: <c>select</c>, <c>insert</c>, <c>update</c>, <c>delete</c>, <c>values</c>, or <c>with_write</c> for a
    /// CTE that writes. Anything else is <c>other</c>.
    /// </summary>
    public static string KindOf(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var words = Normalize(sql).Split([' ', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "other";
        return words[0] switch
        {
            "select" or "insert" or "update" or "delete" or "values" => words[0],
            "with" when words.Any(w => _writeWords.Contains(w)) => "with_write",
            "with" => "select",
            _ => "other",
        };
    }

    /// <summary>
    /// Replaces literals with <c>?</c>, drops comments, turns digit runs and <c>$n</c> parameters into <c>?</c>, collapses
    /// whitespace and lowercases the rest. The result is a fingerprint input, not SQL to run.
    /// </summary>
    internal static string Normalize(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                // Line comment: runs to the end of the line.
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                sb.Append(' ');
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                // Block comment (not nested).
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                sb.Append(' ');
            }
            else if (c == '\'')
            {
                // String literal; a doubled quote is an escaped quote inside the literal.
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append('?');
            }
            else if (c == '$' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]))
            {
                // Positional parameter $1, $2, ...
                i++;
                while (i < sql.Length && char.IsAsciiDigit(sql[i]))
                    i++;
                sb.Append('?');
            }
            else if (char.IsAsciiDigit(c) && !(i > 0 && IsIdentifierChar(sql[i - 1])))
            {
                // A number. A digit inside an identifier (gc_member2) is part of the name and is kept.
                while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] == '.'))
                    i++;
                sb.Append('?');
            }
            else if (char.IsWhiteSpace(c))
            {
                sb.Append(' ');
                i++;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                i++;
            }
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
