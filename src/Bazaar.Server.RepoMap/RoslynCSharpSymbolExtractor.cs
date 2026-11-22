using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bazaar.Server.RepoMap;

/// <summary>
/// Extracts C# declarations from Roslyn syntax trees. No compilation or semantic model is built, so the result is what the source says,
/// not what the names bind to. Malformed source still yields the declarations Roslyn could recognise.
/// </summary>
public sealed class RoslynCSharpSymbolExtractor : ISymbolExtractor
{
    /// <summary>Most symbols kept per file. Larger files are cut in source order.</summary>
    public const int MaxSymbolsPerFile = 2000;

    private const int _maxSignatureLength = 200;

    /// <inheritdoc />
    public bool Supports(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<IReadOnlyList<CodeSymbol>> ExtractAsync(string path, string content, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();

        var tree = CSharpSyntaxTree.ParseText(content, path: path, cancellationToken: ct);
        var root = tree.GetCompilationUnitRoot(ct);
        var symbols = new List<CodeSymbol>();
        foreach (var node in root.DescendantNodes())
        {
            if (symbols.Count >= MaxSymbolsPerFile)
            {
                break;
            }

            var symbol = Describe(node, tree);
            if (symbol is not null)
            {
                symbols.Add(symbol);
            }
        }

        IReadOnlyList<CodeSymbol> result = symbols;
        return Task.FromResult(result);
    }

    /// <summary>Returns the symbol for one declaration node, or null when the node is not a declaration this extractor maps.</summary>
    private static CodeSymbol? Describe(SyntaxNode node, SyntaxTree tree)
    {
        switch (node)
        {
            case TypeDeclarationSyntax type:
                var kind = type switch
                {
                    ClassDeclarationSyntax => SymbolKind.Class,
                    StructDeclarationSyntax => SymbolKind.Struct,
                    InterfaceDeclarationSyntax => SymbolKind.Interface,
                    _ => SymbolKind.Record,
                };
                return Make(kind, type.Identifier, $"{Modifiers(type.Modifiers)} {type.Keyword.ValueText} {type.Identifier.ValueText}{type.TypeParameterList}{type.BaseList}", tree);

            case EnumDeclarationSyntax enumeration:
                return Make(SymbolKind.Enum, enumeration.Identifier, $"{Modifiers(enumeration.Modifiers)} enum {enumeration.Identifier.ValueText}", tree);

            case DelegateDeclarationSyntax del:
                return Make(SymbolKind.Delegate, del.Identifier, $"{Modifiers(del.Modifiers)} delegate {del.ReturnType} {del.Identifier.ValueText}{Parameters(del.ParameterList)}", tree);

            case MethodDeclarationSyntax method:
                return Make(SymbolKind.Method, method.Identifier, $"{Modifiers(method.Modifiers)} {method.ReturnType} {method.Identifier.ValueText}{method.TypeParameterList}{Parameters(method.ParameterList)}", tree);

            case ConstructorDeclarationSyntax constructor:
                return Make(SymbolKind.Constructor, constructor.Identifier, $"{Modifiers(constructor.Modifiers)} {constructor.Identifier.ValueText}{Parameters(constructor.ParameterList)}", tree);

            case PropertyDeclarationSyntax property:
                return Make(SymbolKind.Property, property.Identifier, $"{Modifiers(property.Modifiers)} {property.Type} {property.Identifier.ValueText}", tree);

            default:
                return null;
        }
    }

    private static CodeSymbol? Make(SymbolKind kind, SyntaxToken identifier, string signature, SyntaxTree tree)
    {
        if (identifier.ValueText.Length == 0)
        {
            return null;
        }

        var line = tree.GetLineSpan(identifier.Span).StartLinePosition.Line + 1;
        return new CodeSymbol(kind, identifier.ValueText, line, Collapse(signature));
    }

    private static string Modifiers(SyntaxTokenList modifiers) => string.Join(' ', modifiers.Select(m => m.ValueText));

    /// <summary>Rebuilds the parameter list from its parts, so parameter attributes stay out of the signature.</summary>
    private static string Parameters(ParameterListSyntax list) =>
        "(" + string.Join(", ", list.Parameters.Select(p => Collapse($"{Modifiers(p.Modifiers)} {p.Type} {p.Identifier.ValueText}{(p.Default is null ? string.Empty : " = " + p.Default.Value)}"))) + ")";

    /// <summary>Joins whitespace and caps the length, so every signature stays on one line.</summary>
    private static string Collapse(string signature)
    {
        var joined = string.Join(' ', signature.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return joined.Length <= _maxSignatureLength ? joined : joined[..(_maxSignatureLength - 3)] + "...";
    }
}
