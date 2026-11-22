namespace Bazaar.Rubedo.Views;

/// <summary>
/// What a theme token is, by name the same kinds as the client theme plus <see cref="Interaction"/> for a named motion
/// preset (doc 41c, section 5.1). Rubedo knows kinds, never token names.
/// </summary>
public enum TokenKind
{
    Colour,
    FontFamily,
    Length,
    Flag,
    Number,
    Duration,
    Choice,
    Interaction,
}

/// <summary>
/// The token names a host offers a view validator (doc 41c, section 5.1). Injected so Rubedo does not depend on theme
/// code: the host-side adapter implements it from the theme and motion documents.
/// </summary>
public interface IViewTokenCatalog
{
    /// <summary>Every token name, for completion and docs.</summary>
    IEnumerable<string> Names { get; }

    /// <summary>True when the catalog lists <paramref name="name"/>.</summary>
    bool Contains(string name);

    /// <summary>The kind of <paramref name="name"/>, or null when it is not listed.</summary>
    TokenKind? KindOf(string name);
}

/// <summary>A token reference used as a prop value, written <c>{ "token": "color.hue.3" }</c> on the wire.</summary>
public sealed record ViewTokenRef(string Name);

/// <summary>A simple dictionary-backed catalog, for tests and for hosts that load a name and kind list from a file.</summary>
public sealed class StaticViewTokenCatalog : IViewTokenCatalog
{
    private readonly IReadOnlyDictionary<string, TokenKind> _tokens;

    /// <summary>Creates a catalog over the given names and kinds.</summary>
    public StaticViewTokenCatalog(IReadOnlyDictionary<string, TokenKind> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = tokens;
    }

    /// <inheritdoc />
    public IEnumerable<string> Names => _tokens.Keys;

    /// <inheritdoc />
    public bool Contains(string name) => _tokens.ContainsKey(name);

    /// <inheritdoc />
    public TokenKind? KindOf(string name) => _tokens.TryGetValue(name, out var kind) ? kind : null;
}
