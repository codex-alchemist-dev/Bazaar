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
    /// lease and holds it until the mount is disposed, wipes a stale mount left by a crash (refused and audited when its refs are not all sealed, unless
    /// <see cref="SealedGitMountRequest.AdoptStaleMount"/> reseals them), refuses a mount root the verifier does not confirm, materialises the git dir and a checkout of HEAD
    /// under <see cref="SealedGitMountRequest.CheckoutPath"/>, and returns the mount. The caller calls <see cref="ISealedGitMount.ResealAsync"/>
    /// after every operation that changes the git dir. Disposing zero-wipes the checkout and the scratch folder. Fails closed: a tampered
    /// sealed file throws the store's <c>SealTamperedException</c>, the lease is released and nothing is left on disk; a checkout
    /// path that holds plaintext this repo did not mount is refused. Only one mount (and no other cycle) per repo at a time.
    /// </summary>
    Task<ISealedGitMount> MountAsync(SealedGitMountRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// One-time, audited import of an existing plaintext checkout (a working tree with a <c>.git</c> folder) into this sealed repo. The checkout
    /// must be clean (no changes, untracked or ignored files), on a branch (not detached), with no pending merge, rebase, cherry-pick, revert,
    /// sequencer or bisect, no extra worktrees, no submodules (<c>.gitmodules</c>, <c>.git/modules</c>, gitlink entries), no object alternates
    /// and at most one stash entry. The git dir is sealed in one batch (hooks, config, index, links and per-checkout state are never sealed),
    /// verified in an owner-only folder under <paramref name="mountRoot"/> (every file reads back identically, HEAD matches, <c>git fsck</c>
    /// passes and the checkout is clean, all with git running on our config, not the source's), and only then is the whole plaintext checkout
    /// zero-wiped. A failed verification rolls the sealed copy back and keeps the plaintext. When the sealed repo is not empty and its HEAD
    /// equals the source's (an earlier import whose wipe failed), nothing is sealed again: the source is verified clean and wiped, and 0 is
    /// returned. Any other non-empty repo is refused.
    /// </summary>
    Task<int> ImportCheckoutAsync(string checkoutPath, string mountRoot, IMountRootVerifier verifier, CancellationToken cancellationToken = default);
}

/// <summary>Where a mount (<see cref="ISealedGitRepo.MountAsync"/>) puts its plaintext.</summary>
/// <param name="MountRoot">
/// The only folder plaintext may live in: the encrypted volume of doc 63 or a tmpfs (owner ruling 2026-11-20, DECIDED). It must have no link in
/// any component, is created owner-only when missing, and is refused when (on Unix) open to group or others, or when
/// <paramref name="Verifier"/> does not confirm a protected volume.
/// </param>
/// <param name="CheckoutPath">The working tree of the mount (the git dir is its <c>.git</c> folder). Must be strictly inside <paramref name="MountRoot"/> after canonicalisation.</param>
/// <param name="Verifier">Confirms that the mount root is tmpfs or the encrypted volume.</param>
/// <param name="ScratchPath">
/// Optional folder for worktrees of the mounted repo, strictly inside <paramref name="MountRoot"/> and outside the checkout. Created owner-only,
/// swept when stale and wiped on dispose like the checkout.
/// </param>
/// <param name="AdoptStaleMount">
/// Operator-approved recovery: a stale mount whose refs differ from the sealed ones (commits made but not resealed before a crash) is resealed
/// under the lease with generation checks instead of refused. Off by default: a stale mount sat in plaintext while the server was down.
/// </param>
public sealed record SealedGitMountRequest(string MountRoot, string CheckoutPath, IMountRootVerifier Verifier, string? ScratchPath = null, bool AdoptStaleMount = false);

/// <summary>Checks that a mount root is a protected volume (doc 63 encrypted volume or tmpfs) before plaintext goes there.</summary>
public interface IMountRootVerifier
{
    /// <summary>Returns the verdict for a canonical mount root path.</summary>
    MountRootVerdict Verify(string mountRoot);
}

/// <param name="IsProtected">True when the root is tmpfs, the encrypted volume, or attested by the operator.</param>
/// <param name="Reason">How the verdict was reached, for the audit entry.</param>
public sealed record MountRootVerdict(bool IsProtected, string Reason);

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
