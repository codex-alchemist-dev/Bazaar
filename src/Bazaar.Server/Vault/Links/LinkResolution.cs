namespace Bazaar.Server.Vault.Links;

/// <summary>
/// The outcome of resolving one wiki-link. <see cref="AnchorFound"/> is true when the link has no anchor or the anchor exists in the target;
/// it is always false for an unresolved link. <see cref="Candidates"/> lists every visible match for an ambiguous link and is empty otherwise.
/// </summary>
public sealed record LinkResolution(
    bool Resolved,
    VaultIndexEntry? Target,
    bool AnchorFound,
    IReadOnlyList<VaultIndexEntry> Candidates,
    string? Warning);
