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
    /// without the lease throws a generation conflict and nothing of the batch is committed. Throws <see cref="InvalidOperationException"/>
    /// while the repo is mounted (<see cref="MountAsync"/>).
    /// </summary>
    Task<T> WithWorkingTreeAsync<T>(Func<string, Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>Explicit, audited operator export: writes the plaintext bare repo into <paramref name="targetDir"/> (must be missing or empty) and returns the number of files written. The target is not wiped.</summary>
    Task<int> ExportAsync(string targetDir, CancellationToken cancellationToken = default);

    /// <summary>
    /// Long-lived mount (doc 61 "Vault adoption", PROPOSED) for engines that need a persistent checkout, such as LibGit2. Takes the scope
    /// lease and holds it until the mount is disposed, wipes a stale mount left by a crash, materialises the git dir and a checkout of HEAD
    /// under <see cref="SealedGitMountRequest.CheckoutPath"/>, and returns the mount. The caller calls <see cref="ISealedGitMount.ResealAsync"/>
    /// after every operation that changes the git dir. Disposing zero-wipes the checkout and the scratch folder. Fails closed: a tampered
    /// sealed file throws the store's <c>SealTamperedException</c>, the lease is released and nothing is left on disk; a checkout
    /// path that holds plaintext this repo did not mount is refused. Only one mount (and no other cycle) per repo at a time.
    /// </summary>
    Task<ISealedGitMount> MountAsync(SealedGitMountRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// One-time, audited import of an existing plaintext checkout (a working tree with a <c>.git</c> folder) into this sealed repo, which must
    /// be empty. The checkout must be clean (no changes, untracked or ignored files, pending merge or extra worktrees). The git dir is sealed
    /// in one batch (hooks, config, index, links and per-checkout state are never sealed), verified (every file reads back identically, HEAD
    /// matches and <c>git fsck</c> passes on a restored copy), and only then is the whole plaintext checkout zero-wiped and deleted. Returns the
    /// number of files sealed. Any failure before the wipe leaves the plaintext untouched.
    /// </summary>
    Task<int> ImportCheckoutAsync(string checkoutPath, CancellationToken cancellationToken = default);
}

/// <summary>Where a mount (<see cref="ISealedGitRepo.MountAsync"/>) puts its plaintext.</summary>
/// <param name="MountRoot">
/// The only folder plaintext may live in: the encrypted volume of doc 63 or a tmpfs. Created owner-only when missing; refused when it is a link
/// or (on Unix) open to group or others.
/// </param>
/// <param name="CheckoutPath">The working tree of the mount (the git dir is its <c>.git</c> folder). Must be strictly inside <paramref name="MountRoot"/>.</param>
/// <param name="ScratchPath">
/// Optional folder for worktrees of the mounted repo, strictly inside <paramref name="MountRoot"/> and outside the checkout. Created owner-only,
/// swept when stale and wiped on dispose like the checkout.
/// </param>
public sealed record SealedGitMountRequest(string MountRoot, string CheckoutPath, string? ScratchPath = null);

/// <summary>A mounted sealed repo (<see cref="ISealedGitRepo.MountAsync"/>). Disposing wipes the plaintext and releases the scope lease.</summary>
public interface ISealedGitMount : IAsyncDisposable
{
    /// <summary>The sealed scope, for example <c>vault:acme</c>.</summary>
    string Scope { get; }

    /// <summary>The full path of the checkout (working tree).</summary>
    string Path { get; }

    /// <summary>The full path of the git dir (<c>&lt;Path&gt;/.git</c>).</summary>
    string GitDir { get; }

    /// <summary>The full path of the scratch folder for worktrees, or null.</summary>
    string? ScratchPath { get; }

    /// <summary>True after a reseal failed. A faulted mount refuses further reseals; the plaintext state is not trusted to match the sealed one.</summary>
    bool IsFaulted { get; }

    /// <summary>
    /// Seals every change of the git dir since the last reseal in one all-or-nothing batch. Not sealed: hooks, config, index, links, lock and
    /// temp files, <c>worktrees/</c> metadata, pending merge state and <c>info/</c>. A failure marks the mount faulted and is audited.
    /// </summary>
    Task ResealAsync(CancellationToken cancellationToken = default);
}
