namespace Bazaar.Server.SealedGit;

/// <summary>
/// Opens sealed git repositories (doc 61 Q1). The repo is a bare git repository whose files (packs, loose objects, refs, config)
/// live only as sealed files in scope <c>vault:&lt;name&gt;</c>; a git operation sees a short-lived plaintext copy.
/// </summary>
public interface ISealedGitRepoFactory
{
    /// <summary>Opens the repo for a name such as the company id. The sealed scope is <c>vault:&lt;name&gt;</c>. A new repo is an empty bare repo.</summary>
    ISealedGitRepo Open(string name);
}

/// <summary>One sealed bare git repository.</summary>
public interface ISealedGitRepo
{
    /// <summary>The sealed scope name that holds the repo, for example <c>vault:acme</c>.</summary>
    string Scope { get; }

    /// <summary>
    /// Takes the scope lease (exclusive against other stores and processes for the whole cycle), materialises one consistent snapshot of the
    /// bare repo into a short-lived temporary folder, runs <paramref name="work"/> with that git dir, then re-seals every changed, new or
    /// removed file in one all-or-nothing batch (each with a generation check) and zero-wipes and deletes the folder, also when
    /// <paramref name="work"/> throws. When <paramref name="work"/> throws nothing is re-sealed. A concurrent change to a sealed file made
    /// without the lease throws a generation conflict and nothing of the batch is committed.
    /// </summary>
    Task<T> WithWorkingTreeAsync<T>(Func<string, Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>Explicit, audited operator export: writes the plaintext bare repo into <paramref name="targetDir"/> (must be missing or empty) and returns the number of files written. The target is not wiped.</summary>
    Task<int> ExportAsync(string targetDir, CancellationToken cancellationToken = default);
}
