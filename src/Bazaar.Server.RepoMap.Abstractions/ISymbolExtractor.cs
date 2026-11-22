namespace Bazaar.Server.RepoMap;

/// <summary>
/// Port that extracts declarations from one language's source text. Implementations read syntax only.
/// </summary>
public interface ISymbolExtractor
{
    /// <summary>True when this extractor handles the file at <paramref name="path"/>.</summary>
    bool Supports(string path);

    /// <summary>
    /// Returns the declarations in source order. Malformed source yields whatever could be recognised; it never throws for bad input.
    /// </summary>
    Task<IReadOnlyList<CodeSymbol>> ExtractAsync(string path, string content, CancellationToken ct);
}

/// <summary>The kind of a declaration.</summary>
public enum SymbolKind
{
    /// <summary>A class.</summary>
    Class,

    /// <summary>A struct.</summary>
    Struct,

    /// <summary>An interface.</summary>
    Interface,

    /// <summary>A record.</summary>
    Record,

    /// <summary>An enum.</summary>
    Enum,

    /// <summary>A delegate type.</summary>
    Delegate,

    /// <summary>A method.</summary>
    Method,

    /// <summary>A constructor.</summary>
    Constructor,

    /// <summary>A property.</summary>
    Property,
}

/// <summary>A declaration found in source.</summary>
/// <param name="Kind">The declaration kind.</param>
/// <param name="Name">The declared identifier.</param>
/// <param name="Line">1-based line of the identifier.</param>
/// <param name="Signature">The declaration head on one line, with modifiers and parameters, and without bodies or attributes.</param>
public sealed record CodeSymbol(SymbolKind Kind, string Name, int Line, string Signature);
