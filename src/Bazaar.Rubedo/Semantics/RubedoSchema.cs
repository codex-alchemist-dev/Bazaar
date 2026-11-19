namespace Bazaar.Rubedo.Semantics;

/// <summary>
/// The typed context a head declares (doc 41a section 3). An identifier outside the schema is a compile error, so typos fail at
/// load time. Every evaluation must supply a value of the declared type for each identifier the expression uses.
/// </summary>
public sealed class RubedoSchema
{
    private readonly Dictionary<string, RubedoType> _types;

    public RubedoSchema(IReadOnlyDictionary<string, RubedoType> identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        _types = new Dictionary<string, RubedoType>(identifiers, StringComparer.Ordinal);
    }

    /// <summary>The v1 context from doc 41a section 3.</summary>
    public static RubedoSchema Standard { get; } = new(new Dictionary<string, RubedoType>(StringComparer.Ordinal)
    {
        ["deviceClass"] = RubedoType.Text,
        ["display"] = RubedoType.Text,
        ["posture"] = RubedoType.Text,
        ["widthDp"] = RubedoType.Number,
        ["heightDp"] = RubedoType.Number,
        ["textScale"] = RubedoType.Number,
        ["reduceMotion"] = RubedoType.Bool,
        ["isOwner"] = RubedoType.Bool,
        ["colorScheme"] = RubedoType.Text,
    });

    public IReadOnlyDictionary<string, RubedoType> Identifiers => _types;

    public bool TryGetType(string name, out RubedoType type) => _types.TryGetValue(name, out type);
}
