using System.Globalization;
using System.Text;
using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Syntax;

namespace Bazaar.Rubedo;

/// <summary>Result of formatting. Formatted is null whenever Diagnostics is not empty.</summary>
public sealed record RubedoFormatResult(string? Formatted, IReadOnlyList<RubedoDiagnostic> Diagnostics)
{
    public bool IsValid => Formatted is not null;
}

/// <summary>
/// Canonical formatter (doc 41a section 13, PROPOSED). Prints the parsed tree, so the result always parses to the same tree
/// and evaluates the same. Never throws: source that does not compile returns the compile diagnostics unchanged.
/// </summary>
public static class RubedoFormatter
{
    private const int _unaryPrecedence = 7;
    private const int _atomPrecedence = 8;

    public static RubedoFormatResult Format(string source, RubedoSchema? schema = null)
    {
        RubedoCompileResult compiled = RubedoEngine.Compile(source, schema);
        if (!compiled.IsValid)
        {
            return new RubedoFormatResult(null, compiled.Diagnostics);
        }

        var builder = new StringBuilder();
        Write(builder, compiled.Program!.Root);
        string formatted = builder.ToString();

        // The canonical form can be longer than the input (spaces around operators), so check the limits on it too.
        RubedoCompileResult again = RubedoEngine.Compile(formatted, schema, options: compiled.Program.Options);
        return again.IsValid
            ? new RubedoFormatResult(formatted, [])
            : new RubedoFormatResult(null, again.Diagnostics);
    }

    private static void Write(StringBuilder sb, Node node)
    {
        switch (node)
        {
            case LiteralNode literal:
                WriteLiteral(sb, literal);
                break;
            case IdentifierNode identifier:
                sb.Append(identifier.Name);
                break;
            case UnaryNode unary:
                sb.Append(unary.Op);
                WriteChild(sb, unary.Operand, Precedence(unary.Operand) < _unaryPrecedence);
                break;
            case BinaryNode binary:
                int own = Precedence(binary);
                int left = Precedence(binary.Left);
                int right = Precedence(binary.Right);

                // Comparisons and equality do not chain, and the parser rejects a relation directly under == or !=, so any
                // child of those two levels is parenthesised. The other operators are left-associative, so only a right
                // child of the same level needs parentheses.
                bool nonAssociative = own is 3 or 4;
                WriteChild(sb, binary.Left, nonAssociative ? left is 3 or 4 : left < own);
                sb.Append(' ').Append(binary.Op).Append(' ');
                WriteChild(sb, binary.Right, nonAssociative ? right is 3 or 4 : right <= own);
                break;
            case CallNode call:
                sb.Append(call.Name).Append('(');
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(sb, call.Arguments[i]);
                }

                sb.Append(')');
                break;
            default:
                throw new InvalidOperationException($"Unknown node {node.GetType().Name}.");
        }
    }

    private static void WriteChild(StringBuilder sb, Node child, bool parenthesise)
    {
        if (parenthesise)
        {
            sb.Append('(');
        }

        Write(sb, child);
        if (parenthesise)
        {
            sb.Append(')');
        }
    }

    private static void WriteLiteral(StringBuilder sb, LiteralNode literal)
    {
        switch (literal.LiteralType)
        {
            case RubedoType.Bool:
                sb.Append((bool)literal.Value ? "true" : "false");
                break;
            case RubedoType.Text:
                sb.Append('"').Append((string)literal.Value).Append('"');
                break;
            default:
                sb.Append(NormaliseNumber((decimal)literal.Value));
                break;
        }
    }

    /// <summary>Drops leading zeros (the decimal prints its own digits) and trailing fraction zeros. The value is identical.</summary>
    private static string NormaliseNumber(decimal value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.'))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text;
    }

    private static int Precedence(Node node) => node switch
    {
        BinaryNode b => b.Op switch
        {
            "||" => 1,
            "&&" => 2,
            "==" or "!=" => 3,
            "<" or "<=" or ">" or ">=" => 4,
            "+" or "-" => 5,
            _ => 6,
        },
        UnaryNode => _unaryPrecedence,
        _ => _atomPrecedence,
    };
}
