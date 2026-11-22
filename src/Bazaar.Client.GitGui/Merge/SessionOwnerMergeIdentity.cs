namespace Bazaar.Client.GitGui.Merge;

/// <summary>The signed-in person as the merge button needs them: the principal id and the name and email shown on their account.</summary>
public sealed record MergePrincipal(Guid HumanId, string DisplayName, string Email);

/// <summary>
/// The per-human sign-off profile field (doc 52, Merge identity). No profile store exists yet, so the default returns null,
/// meaning "unset".
/// </summary>
public interface IMergeProfileStore
{
    /// <summary>The person's own full sign-off line, or null when unset.</summary>
    string? GetSignOff(Guid humanId);
}

/// <summary>The shipped profile store: every field is unset.</summary>
public sealed class UnsetMergeProfileStore : IMergeProfileStore
{
    /// <inheritdoc />
    public string? GetSignOff(Guid humanId) => null;
}

/// <summary>
/// The merge identity of whoever is signed in: their principal id, and the sign-off line from their profile field, or
/// <c>Signed-off-by: Display Name &lt;email&gt;</c> when the field is unset (the repo's DCO format).
/// </summary>
public sealed class SessionOwnerMergeIdentity : IOwnerMergeIdentity
{
    private const string _prefix = "Signed-off-by: ";

    private SessionOwnerMergeIdentity(Guid humanId, string signOff)
    {
        HumanId = humanId;
        SignOff = signOff;
    }

    /// <inheritdoc />
    public Guid HumanId { get; }

    /// <inheritdoc />
    public string SignOff { get; }

    /// <summary>
    /// Builds the identity, or returns null when no one is signed in or no valid sign-off line can be formed (the card then says
    /// this client cannot approve). A profile value that is not one <c>Signed-off-by:</c> line is ignored.
    /// </summary>
    public static IOwnerMergeIdentity? TryCreate(MergePrincipal? principal, IMergeProfileStore? profile = null)
    {
        if (principal is null || principal.HumanId == Guid.Empty)
        {
            return null;
        }

        var custom = profile?.GetSignOff(principal.HumanId)?.Trim();
        if (custom is { Length: > 0 } && custom.StartsWith(_prefix, StringComparison.Ordinal)
            && custom.Length > _prefix.Length && !custom.Any(char.IsControl))
        {
            return new SessionOwnerMergeIdentity(principal.HumanId, custom);
        }

        var name = Clean(principal.DisplayName);
        var email = Clean(principal.Email);
        return name.Length == 0 || email.Length == 0
            ? null
            : new SessionOwnerMergeIdentity(principal.HumanId, $"{_prefix}{name} <{email}>");
    }

    private static string Clean(string? value) =>
        value is null ? string.Empty : string.Concat(value.Where(c => !char.IsControl(c) && c != '<' && c != '>')).Trim();
}
