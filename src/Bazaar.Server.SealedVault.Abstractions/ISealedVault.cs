namespace Bazaar.Server.SealedVault;

/// <summary>
/// The sealed Vault repo (doc 61 "Vault adoption", PROPOSED): the company Vault git repo kept sealed at rest and mounted in plaintext only under
/// the operator's mount root while the server runs. Exposed for health and admin views; Vault callers keep using <c>IGitProvider</c>.
/// </summary>
public interface ISealedVault
{
    /// <summary>The sealed scope of the repo, for example <c>vault:acme</c>.</summary>
    string Scope { get; }

    /// <summary>The Vault's main checkout path (the mount's checkout).</summary>
    string RepoPath { get; }

    /// <summary>True while the repo is mounted.</summary>
    bool IsMounted { get; }

    /// <summary>True after a reseal failed; every Vault git call then fails closed until the server remounts.</summary>
    bool IsFaulted { get; }

    /// <summary>Imports a configured plaintext repo when one is waiting, then mounts. Vault git calls do this on first use; a host may call it at start.</summary>
    Task EnsureMountedAsync(CancellationToken cancellationToken = default);
}
