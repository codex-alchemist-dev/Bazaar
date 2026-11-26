using Bazaar.Client.Motion;
using Bazaar.Rubedo.Views;
using ThemeKind = Bazaar.Client.Theme.TokenKind;

namespace Bazaar.Client.Rubedo.Tokens;

/// <summary>
/// The view validator's token catalog (doc 41c, section 5.1), built from the client theme's required tokens plus any
/// extra tokens the host registers (the doc 93 hue, material, motion, layout and gesture tokens, and the loaded
/// <c>motion.interaction.&lt;name&gt;</c> presets). The theme names its kinds, Rubedo names its own; this class maps one to the other.
/// The view validator's token catalog (doc 41c, section 5.1): every token in the client theme's required list (which
/// carries the doc 93 hue, material and motion tokens), the shipped <c>motion.interaction.&lt;name&gt;</c> presets, the
/// layout and gesture tokens doc 41c lists, and any extra tokens the host registers. The theme names its kinds, Rubedo
/// names its own; this class maps one to the other.
/// </summary>
public sealed class ThemeViewTokenCatalog : IViewTokenCatalog
{
    private readonly Dictionary<string, TokenKind> _tokens;

    /// <summary>Creates a catalog of the theme's required tokens and <paramref name="extra"/>. An extra with the same name wins.</summary>
    /// <summary>
    /// Layout and gesture tokens the GUI work adds (doc 41c section 5.1, from the desktop chrome PR). They are plain numbers
    /// the theme layer range-checks; they are listed here until they join <c>ThemeTokens.Required</c>, and an entry there wins.
    /// </summary>
    public static IReadOnlyList<string> PendingNumberTokens { get; } =
    [
        "layout.sidebar.width.expanded",
        "layout.sidebar.width.collapsed",
        "layout.strip.height",
        "layout.sheet.width",
        "layout.sheet.side.width",
        "material.scrim.opacity",
        "gesture.sheet.dismiss.fraction",
    ];

    /// <summary>Creates a catalog of the theme's required tokens, the interaction presets, the pending tokens and <paramref name="extra"/>. An extra with the same name wins.</summary>
    public ThemeViewTokenCatalog(IEnumerable<KeyValuePair<string, TokenKind>>? extra = null)
    {
        _tokens = new Dictionary<string, TokenKind>(StringComparer.Ordinal);
        foreach (var spec in Bazaar.Client.Theme.ThemeTokens.Required)
        {
            _tokens[spec.Name] = Map(spec.Kind);
        }

        foreach (var name in PendingNumberTokens)
        {
            _tokens.TryAdd(name, TokenKind.Number);
        }

        foreach (var name in InteractionNames.All)
        {
            _tokens[name] = TokenKind.Interaction;
        }

        if (extra is not null)
        {
            foreach (var (name, kind) in extra)
            {
                _tokens[name] = kind;
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> Names => _tokens.Keys.Order(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool Contains(string name) => _tokens.ContainsKey(name);

    /// <inheritdoc />
    public TokenKind? KindOf(string name) => _tokens.TryGetValue(name, out var kind) ? kind : null;

    private static TokenKind Map(ThemeKind kind) => kind switch
    {
        ThemeKind.Colour => TokenKind.Colour,
        ThemeKind.FontFamily => TokenKind.FontFamily,
        ThemeKind.Length => TokenKind.Length,
        ThemeKind.Flag => TokenKind.Flag,
        ThemeKind.Number => TokenKind.Number,
        ThemeKind.Duration => TokenKind.Duration,
        ThemeKind.Choice => TokenKind.Choice,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A theme token kind with no Rubedo counterpart."),
    };
}
