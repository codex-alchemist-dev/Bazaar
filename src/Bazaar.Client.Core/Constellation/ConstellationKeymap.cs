namespace Bazaar.Client.Core.Constellation;

/// <summary>
/// Mnemonic keys for Constellation navigation (27, keyboard model). Each key is one character, never a Ctrl chord.
/// The default table is data: a settings change builds its own map with <see cref="Merge"/>, which validates each key.
///
/// Default table:
///   f   Focus            (the caller supplies the node)
///   b   Back
///   n   NextNeighbour
///   p   PreviousNeighbour
///   +   ZoomIn
///   -   ZoomOut
///
/// Forward has no default key. Bind it through an override or the command palette.
/// </summary>
public static class ConstellationKeymap
{
    /// <summary>Shipped key table. Read only; use <see cref="Merge"/> to build an overridden copy.</summary>
    public static readonly IReadOnlyDictionary<string, ConstellationCommandKind> Defaults =
        new Dictionary<string, ConstellationCommandKind>(StringComparer.Ordinal)
        {
            ["f"] = ConstellationCommandKind.Focus,
            ["b"] = ConstellationCommandKind.Back,
            ["n"] = ConstellationCommandKind.NextNeighbour,
            ["p"] = ConstellationCommandKind.PreviousNeighbour,
            ["+"] = ConstellationCommandKind.ZoomIn,
            ["-"] = ConstellationCommandKind.ZoomOut,
        };

    /// <summary>
    /// Returns a new table: the defaults with <paramref name="overrides"/> applied. Each override key must be a single
    /// printable character (not a control character, so no Ctrl chords). Throws <see cref="ArgumentException"/> otherwise.
    /// </summary>
    public static IReadOnlyDictionary<string, ConstellationCommandKind> Merge(
        IReadOnlyDictionary<string, ConstellationCommandKind> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var merged = new Dictionary<string, ConstellationCommandKind>(Defaults, StringComparer.Ordinal);
        foreach ((string key, ConstellationCommandKind kind) in overrides)
        {
            ValidateKey(key);
            merged[key] = kind;
        }

        return merged;
    }

    /// <summary>Looks up a key in the default table.</summary>
    public static bool TryResolve(string key, out ConstellationCommandKind kind) => TryResolve(Defaults, key, out kind);

    /// <summary>Looks up a key in <paramref name="map"/>. Returns false for a null, empty or unbound key.</summary>
    public static bool TryResolve(
        IReadOnlyDictionary<string, ConstellationCommandKind> map,
        string? key,
        out ConstellationCommandKind kind)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (key is null)
        {
            kind = default;
            return false;
        }

        return map.TryGetValue(key, out kind);
    }

    /// <summary>Throws unless the key is one printable, non-whitespace character.</summary>
    public static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 1 || char.IsControl(key[0]) || char.IsWhiteSpace(key[0]))
        {
            throw new ArgumentException(
                $"Constellation key '{key}' must be one printable character. Ctrl chords are not allowed.",
                nameof(key));
        }
    }
}
