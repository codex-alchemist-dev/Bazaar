using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// Decides which tool definitions a turn offers the model. Pure, so the rule is testable without a database.
/// A reader turn never offers tools; an actor turn offers the supplied definitions.
/// </summary>
public static class TurnToolOffer
{
    /// <summary>The tools offered for a turn of <paramref name="role"/>: none for a reader, the supplied list for an actor.</summary>
    public static IReadOnlyList<ModelToolDefinition> ForRole(TurnRole role, IReadOnlyList<ModelToolDefinition>? supplied) =>
        role == TurnRole.Reader ? [] : supplied ?? [];

    /// <summary>
    /// The catalogue entries named in <paramref name="allowlist"/>, in catalogue order, for a turn of <paramref name="role"/>.
    /// A reader turn gets none, whatever the allowlist says.
    /// </summary>
    public static IReadOnlyList<ModelToolDefinition> Select(
        TurnRole role, IReadOnlyList<ModelToolDefinition> catalogue, IReadOnlyList<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(allowlist);
        if (role == TurnRole.Reader || catalogue.Count == 0 || allowlist.Count == 0)
            return [];

        var allowed = allowlist.ToHashSet(StringComparer.Ordinal);
        return catalogue.Where(t => allowed.Contains(t.Name)).ToArray();
    }
}
