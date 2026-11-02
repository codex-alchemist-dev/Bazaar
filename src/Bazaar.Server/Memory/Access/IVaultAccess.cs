namespace Bazaar.Server.Memory.Access;

/// <summary>
/// The Vault access layer. Every Vault read, search result, link resolution and index query goes through it, because
/// git itself has no ACLs (docs/design/08-memory-system.md, section 8). Pure rules: no I/O.
/// </summary>
public interface IVaultAccess
{
    /// <summary>True when <paramref name="who"/> may read <paramref name="path"/>.</summary>
    bool CanRead(VaultPrincipal who, VaultPath path);

    /// <summary>
    /// Returns only the paths <paramref name="who"/> may read, in their original order. Search hits go through this
    /// and nothing else, so hidden paths are never returned.
    /// </summary>
    IReadOnlyList<VaultPath> FilterVisible(VaultPrincipal who, IEnumerable<VaultPath> paths);

    /// <summary>The decision for <paramref name="who"/> reading <paramref name="path"/>, with its reason and logging flag.</summary>
    AccessDecision Explain(VaultPrincipal who, VaultPath path);

    /// <summary>
    /// True when a link from <paramref name="fromPath"/> to <paramref name="toPath"/> may be resolved for
    /// <paramref name="who"/>. A hidden target is refused, so private targets never leak through link resolution.
    /// </summary>
    bool CanResolveLink(VaultPrincipal who, VaultPath fromPath, VaultPath toPath);
}
