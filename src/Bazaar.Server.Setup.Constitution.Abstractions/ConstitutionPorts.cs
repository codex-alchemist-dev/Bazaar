namespace Bazaar.Server.Setup.Constitution;

/// <summary>What <see cref="IConstitutionVault.EnsureAsync"/> found or made.</summary>
/// <param name="HeadCommit">The Vault head commit after the call; the Constitution is indexed at this commit.</param>
/// <param name="Created">True when this call wrote the Constitution; false when the file was already in the Vault.</param>
public sealed record ConstitutionSeedResult(string HeadCommit, bool Created);

/// <summary>A failure the operator can act on. The message names settings and outcomes, never a secret or a document body.</summary>
public sealed class ConstitutionVaultException(string message) : Exception(message);

/// <summary>
/// The Vault side of the Constitution seed (docs/design/24-company-systems-core.md section 1, docs/design/45-onboarding.md
/// section 1.15). The Vault, a git repository of Markdown, is the source of truth; the Postgres index is rebuildable from it.
/// </summary>
public interface IConstitutionVault
{
    /// <summary>The Vault path of the Constitution, relative to the repository root.</summary>
    public const string Path = "company/Constitution.md";

    /// <summary>
    /// Makes sure the Constitution exists in the Vault and is indexed as a <c>constitution</c> document. An existing file is
    /// never overwritten, because the owner may have edited it. Idempotent.
    /// </summary>
    /// <exception cref="ConstitutionVaultException">The Vault is not configured, or the document is not valid.</exception>
    Task<ConstitutionSeedResult> EnsureAsync(string markdown, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Vault repository and the identity that commits to it. These are host facts, not registry settings (the same keys as the
/// memory module, so one install configures one Vault). There is no default identity or domain.
/// </summary>
/// <param name="RepoPath">The Vault's main checkout. Created and initialised when it does not exist yet.</param>
/// <param name="VaultId">The Vault id the index rows carry.</param>
/// <param name="WriterName">The commit author name.</param>
/// <param name="WriterEmail">The commit author email.</param>
public sealed record ConstitutionVaultOptions(string RepoPath, Guid VaultId, string WriterName, string WriterEmail)
{
    /// <summary>Configuration key of <see cref="RepoPath"/>.</summary>
    public const string RepoPathKey = "Memory:Vault:RepoPath";

    /// <summary>Configuration key of <see cref="VaultId"/>.</summary>
    public const string VaultIdKey = "Memory:Vault:VaultId";

    /// <summary>Configuration key of <see cref="WriterName"/>.</summary>
    public const string WriterNameKey = "Memory:Vault:WriterName";

    /// <summary>Configuration key of <see cref="WriterEmail"/>.</summary>
    public const string WriterEmailKey = "Memory:Vault:WriterEmail";

    /// <summary>Reads the options through <paramref name="read"/>. Null when any of the four values is missing or malformed.</summary>
    public static ConstitutionVaultOptions? FromSettings(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var repo = read(RepoPathKey);
        var name = read(WriterNameKey);
        var email = read(WriterEmailKey);
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)
            || !Guid.TryParse(read(VaultIdKey), out var vaultId))
        {
            return null;
        }

        return new ConstitutionVaultOptions(repo, vaultId, name, email);
    }
}
