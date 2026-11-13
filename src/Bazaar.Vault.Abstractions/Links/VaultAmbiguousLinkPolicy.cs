namespace Bazaar.Server.Vault.Links;

/// <summary>
/// What the resolver does when a title or alias link matches several visible documents, after the same-project tie-break.
/// <see cref="Warn"/> links to the first candidate by path and reports the others; <see cref="Refuse"/> leaves the link unresolved.
/// </summary>
public enum VaultAmbiguousLinkPolicy
{
    Warn,
    Refuse,
}
