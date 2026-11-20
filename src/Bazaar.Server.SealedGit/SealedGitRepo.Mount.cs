using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedGit;

/// <summary>
/// The long-lived mount and the plaintext import (doc 61 "Vault adoption"). A mount keeps the git dir and a checkout of HEAD under an
/// operator-chosen mount root (tmpfs or the doc 63 encrypted volume only, owner ruling 2026-11-20) for as long as the engine needs them, holds
/// the scope lease for that whole time, and reseals the git dir after each mutating operation through the same batch path as
/// <see cref="WithWorkingTreeAsync{T}"/>.
/// </summary>
public sealed partial class SealedGitRepo
{
    /// <summary>Suffix of the marker file next to a mounted folder: the folder is ours. A stale one is wiped only when it holds no unsealed refs.</summary>
    internal const string MountMarkerSuffix = ".bazaar-sealed-mount";

    private const string _noHooksName = "bazaar-nohooks";
    private const string _importPrefix = "bazaar-sealedgit-import-";

    /// <summary>Git-dir paths a mount never seals: per-checkout state the next mount rebuilds, and anything that is configuration.</summary>
    private static readonly HashSet<string> _mountLocalFiles = new(StringComparer.Ordinal)
    {
        "config", "index", "description", "COMMIT_EDITMSG", "ORIG_HEAD", "FETCH_HEAD", "AUTO_MERGE",
        "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "MERGE_RR", "CHERRY_PICK_HEAD", "REVERT_HEAD",
    };

    private static readonly string[] _mountLocalPrefixes =
        ["worktrees/", "info/", "logs/", "modules/", "sequencer/", "rebase-merge/", "rebase-apply/", "BISECT_", _noHooksName + "/", "bazaar-"];

    /// <summary>Operations in progress that an import refuses (L2): the checkout would be wiped mid-operation.</summary>
    private static readonly string[] _inProgress = ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer"];

    private int _mounted;

    [GeneratedRegex(@"^objects/(?:[0-9a-f]{2}/[0-9a-f]{38,62}|pack/pack-[0-9a-f]{40,64}\.(?:pack|idx|rev|bitmap))$", RegexOptions.CultureInvariant)]
    private static partial Regex ImmutableObject();

    private static bool IsImmutableObject(string logical) => ImmutableObject().IsMatch(logical);

    /// <summary>What a mount (and an import) seals: <see cref="Sealable"/> minus per-checkout state, config, reflogs and worktree metadata.</summary>
    internal static bool MountSealable(string logical) =>
        Sealable(logical)
        && !_mountLocalFiles.Contains(logical)
        && !_mountLocalPrefixes.Any(p => logical.StartsWith(p, StringComparison.Ordinal));

    private static bool IsRefFile(string logical) => logical is "HEAD" or "packed-refs" || logical.StartsWith("refs/", StringComparison.Ordinal);

    private void ThrowIfMounted()
    {
        if (Volatile.Read(ref _mounted) != 0)
        {
            throw new InvalidOperationException($"Sealed repo {Scope} is mounted; use the mount, or dispose it first.");
        }
    }

    public async Task<ISealedGitMount> MountAsync(SealedGitMountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Verifier);
        var (root, checkout, scratch) = ValidateMountRequest(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfMounted();
            PrepareMountRoot(root);
            SweepImportLeftovers(root);
            await RequireProtectedRootAsync(root, request.Verifier).ConfigureAwait(false);

            var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
            ISealedLease? lease = null;
            var owned = new List<string>();
            try
            {
                lease = await scope.AcquireLeaseAsync(_options.LeaseTimeout, cancellationToken).ConfigureAwait(false);
                AuditUnknownMarkers(root, scratch is null ? [checkout] : [checkout, scratch]);
                await HandleStaleCheckoutAsync(scope, checkout, request.AdoptStaleMount).ConfigureAwait(false);

                var folders = scratch is null ? new[] { checkout } : [checkout, scratch];
                foreach (var dir in folders)
                {
                    await ClaimMountFolderAsync(root, dir).ConfigureAwait(false);
                    owned.Add(dir);
                }

                var gitDir = Path.Combine(checkout, ".git");
                var snapshot = await MaterialiseAsync(scope, gitDir, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(gitDir, "config"), MountConfig(), cancellationToken).ConfigureAwait(false);
                await CheckoutHeadAsync(gitDir, checkout, snapshot, cancellationToken).ConfigureAwait(false);

                await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.mount", Scope, null, $"Mounted in plaintext at {checkout} under mount root {root}"), CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _mounted, 1);
                return new Mount(this, scope, lease, checkout, gitDir, scratch, snapshot);
            }
            catch (Exception ex)
            {
                // Only folders this call claimed are wiped; a folder with plaintext we did not mount, or with unsealed refs, is left for the operator.
                foreach (var dir in owned)
                {
                    WipeMountFolder(dir);
                }

                lease?.Dispose();
                scope.Dispose();
                AuditSafe(_audit, "sealgit.mount.failed", Scope, $"Mount at {checkout} failed: {ex.GetType().Name}");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ImportCheckoutAsync(string checkoutPath, string mountRoot, IMountRootVerifier verifier, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRoot);
        ArgumentNullException.ThrowIfNull(verifier);
        var root = ValidateRoot(mountRoot);
        var source = SealedPaths.Canonicalize(checkoutPath);
        if (SealedPaths.IsAtOrInside(source, root) || SealedPaths.IsStrictlyInside(root, source))
        {
            throw new InvalidOperationException("The import source must be outside the mount root.");
        }

        var gitDir = Path.Combine(source, ".git");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfMounted();
            await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.import.start", Scope, null, $"Import of {source}"), CancellationToken.None).ConfigureAwait(false);
            try
            {
                PrepareMountRoot(root);
                SweepImportLeftovers(root);
                await RequireProtectedRootAsync(root, verifier).ConfigureAwait(false);
                var count = await ImportCoreAsync(source, gitDir, root, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, count > 0 ? "sealgit.import" : "sealgit.import.already_done", Scope, null, $"{count} git files sealed and verified; plaintext checkout {source} wiped"), CancellationToken.None).ConfigureAwait(false);
                return count;
            }
            catch (Exception ex)
            {
                AuditSafe(_audit, "sealgit.import.failed", Scope, $"Import of {source} failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ImportCoreAsync(string source, string gitDir, string root, CancellationToken ct)
    {
        var files = CheckImportSource(source, gitDir);
        var temp = Path.Combine(root, _importPrefix + Guid.NewGuid().ToString("N"));
        SecureTemp.CreateOwnerOnly(temp);
        try
        {
            var noHooks = Path.Combine(temp, "nohooks");
            var head = await ResolveHeadAsync(gitDir, null, noHooks, ct).ConfigureAwait(false);
            var coreExtras = await ReadLineEndingConfigAsync(gitDir, Path.Combine(temp, "probe"), noHooks).ConfigureAwait(false);

            using var scope = await _store.OpenScopeAsync(Scope, ct).ConfigureAwait(false);
            using var lease = await scope.AcquireLeaseAsync(_options.LeaseTimeout, ct).ConfigureAwait(false);
            var verifyDir = Path.Combine(temp, "verify");
            if ((await scope.ListAsync(ct).ConfigureAwait(false)).Count > 0)
            {
                // An earlier import sealed and verified this source but its wipe failed: finish the wipe, seal nothing.
                var restored = Path.Combine(verifyDir, "git");
                var sealedSnapshot = await MaterialiseAsync(scope, restored, CancellationToken.None).ConfigureAwait(false);
                var sealedHead = await ResolveHeadAsync(restored, sealedSnapshot, noHooks, CancellationToken.None).ConfigureAwait(false);
                if (head is null || sealedHead != head)
                {
                    throw new InvalidOperationException($"Sealed repo {Scope} is not empty and its HEAD differs from the source; an import only goes into an empty repo.");
                }

                await VerifyCleanAsync(restored, source, head, coreExtras, noHooks).ConfigureAwait(false);
                WipeImportedSource(source);
                return 0;
            }

            var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var batch = new List<SealedWrite>();
            var buffers = new List<byte[]>();
            try
            {
                foreach (var file in files.OrderBy(f => f.FullName, StringComparer.Ordinal))
                {
                    var logical = Path.GetRelativePath(gitDir, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                    if (!MountSealable(logical))
                    {
                        continue;
                    }

                    if (file.Length > MaxFileBytes)
                    {
                        throw new InvalidOperationException($"'{logical}' is larger than the 256 MiB sealed file limit. Nothing was imported.");
                    }

                    var bytes = await File.ReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
                    buffers.Add(bytes);
                    hashes[logical] = SHA256.HashData(bytes);
                    batch.Add(SealedWrite.Put(logical, bytes, 0));
                }

                if (batch.Count > 0)
                {
                    await scope.WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var bytes in buffers)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            try
            {
                await VerifyImportAsync(scope, hashes, head, source, verifyDir, coreExtras, noHooks).ConfigureAwait(false);
            }
            catch
            {
                await RollBackImportAsync(scope, hashes.Keys).ConfigureAwait(false);
                throw;
            }

            WipeImportedSource(source);
            return batch.Count;
        }
        finally
        {
            if (SecureTemp.WipeAndDelete(temp) is { } tempFailure)
            {
                AuditSafe(_audit, "sealgit.wipe_failed", Scope, $"Wipe of {temp} failed: {tempFailure.Message}");
            }
        }
    }

    /// <summary>Test seam: the verification of a fresh import. Internal so tests can force a failure and check the rollback.</summary>
    internal Func<string, Task>? AfterImportSealedForTest { get; set; }

    private async Task RollBackImportAsync(ISealedScope scope, IEnumerable<string> paths)
    {
        // Roll the sealed copy back so the operator can retry; the plaintext was never touched.
        try
        {
            var deletes = paths.Select(p => SealedWrite.Delete(p)).ToList();
            if (deletes.Count > 0)
            {
                await scope.WriteBatchAsync(deletes, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AuditSafe(_audit, "sealgit.import.rollback_failed", Scope, $"Rollback after a failed verification failed: {ex.GetType().Name}");
        }
    }

    private void WipeImportedSource(string source)
    {
        var failure = SecureTemp.WipeAndDelete(source);
        if (failure is not null)
        {
            AuditSafe(_audit, "sealgit.wipe_failed", Scope, $"Wipe of the imported plaintext {source} failed: {failure.Message}");
            throw new IOException("The repo is sealed and verified, but the plaintext checkout could not be fully wiped; see the sealgit.wipe_failed audit event. A retry finishes the wipe.", failure);
        }
    }

    /// <summary>Filesystem refusals before anything is read (M2, M3, L2, L3). Returns the files of the git dir.</summary>
    private static List<FileInfo> CheckImportSource(string source, string gitDir)
    {
        var sourceInfo = new DirectoryInfo(source);
        var gitInfo = new DirectoryInfo(gitDir);
        if (!sourceInfo.Exists || SecureTemp.IsLink(sourceInfo))
        {
            throw new InvalidOperationException("The import source must be an existing folder, not a link.");
        }

        if (!gitInfo.Exists || SecureTemp.IsLink(gitInfo))
        {
            throw new InvalidOperationException("The import source must hold a .git folder (not a .git file or link).");
        }

        var (files, links, _) = SecureTemp.Walk(gitDir);
        if (links.Count > 0)
        {
            throw new InvalidOperationException($"The source git dir contains a link ('{Path.GetRelativePath(gitDir, links[0].FullName)}'); nothing was imported.");
        }

        var head = File.Exists(Path.Combine(gitDir, "HEAD")) ? File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim() : string.Empty;
        if (!head.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The source HEAD is detached or not a branch; check out a branch first.");
        }

        foreach (var name in _inProgress)
        {
            if (File.Exists(Path.Combine(gitDir, name)) || Directory.Exists(Path.Combine(gitDir, name)))
            {
                throw new InvalidOperationException($"The source has an operation in progress ({name}); finish or abort it first.");
            }
        }

        if (Directory.EnumerateFileSystemEntries(gitDir, "BISECT_*").Any())
        {
            throw new InvalidOperationException("The source has a bisect in progress; run git bisect reset first.");
        }

        var worktrees = Path.Combine(gitDir, "worktrees");
        if (Directory.Exists(worktrees) && Directory.EnumerateFileSystemEntries(worktrees).Any())
        {
            throw new InvalidOperationException("The source has worktrees; remove and prune them first.");
        }

        if (Directory.Exists(Path.Combine(gitDir, "modules")) || File.Exists(Path.Combine(source, ".gitmodules")))
        {
            throw new InvalidOperationException("The source uses submodules; they cannot be sealed. Remove them first.");
        }

        if (_alternates.Any(a => File.Exists(Path.Combine(gitDir, a.Replace('/', Path.DirectorySeparatorChar)))))
        {
            throw new InvalidOperationException("The source uses object alternates; repack without them (git repack -a -d) and remove the alternates file first.");
        }

        var stashLog = Path.Combine(gitDir, "logs", "refs", "stash");
        if (File.Exists(stashLog) && File.ReadLines(stashLog).Count(l => l.Trim().Length > 0) > 1)
        {
            throw new InvalidOperationException("The source has more than one stash entry; only the newest is kept by a seal. Apply or drop the others first.");
        }

        return files;
    }

    /// <summary>
    /// Checks the sealed copy against the source: same files and bytes, same HEAD, <c>git fsck</c> clean, no gitlink entry, and the source
    /// checkout has no change, untracked or ignored file. Every git call runs on the restored copy, whose config is ours, so the source's config
    /// (filters, fsmonitor, aliases) is never used.
    /// </summary>
    private async Task VerifyImportAsync(ISealedScope scope, Dictionary<string, byte[]> expected, string? head, string source, string verifyDir, string coreExtras, string noHooks)
    {
        var sealedFiles = await scope.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (sealedFiles.Files.Count != expected.Count)
            {
                throw new SealTamperedException(Scope, null, "The imported repo did not read back with the same files; plaintext kept.");
            }

            foreach (var file in sealedFiles.Files)
            {
                if (!expected.TryGetValue(file.Path, out var hash) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file.Content), hash))
                {
                    throw new SealTamperedException(Scope, file.Path, "An imported file did not read back identically; plaintext kept.");
                }
            }
        }
        finally
        {
            foreach (var file in sealedFiles.Files)
            {
                CryptographicOperations.ZeroMemory(file.Content);
            }
        }

        var gitDir = Path.Combine(verifyDir, "git");
        var snapshot = await MaterialiseAsync(scope, gitDir, CancellationToken.None).ConfigureAwait(false);
        if (AfterImportSealedForTest is { } hook)
        {
            await hook(gitDir).ConfigureAwait(false);
        }

        if (await ResolveHeadAsync(gitDir, snapshot, noHooks, CancellationToken.None).ConfigureAwait(false) != head)
        {
            throw new InvalidOperationException("HEAD of the restored sealed repo differs from the source; plaintext kept.");
        }

        var fsck = await GitCommand.RunWithHooksDirAsync(gitDir, ["fsck", "--no-progress", "--no-dangling"], null, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
        if (fsck.ExitCode != 0)
        {
            throw new InvalidOperationException("git fsck failed on the restored sealed repo; plaintext kept.");
        }

        if (head is not null)
        {
            var tree = await GitCommand.RunWithHooksDirAsync(gitDir, ["ls-tree", "-r", "-z", "HEAD"], null, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
            if (tree.ExitCode != 0)
            {
                throw new InvalidOperationException("Listing the restored HEAD tree failed; plaintext kept.");
            }

            if (tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(e => e.StartsWith("160000 ", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("HEAD contains a submodule (gitlink) entry; it cannot be sealed. Plaintext kept.");
            }
        }

        await VerifyCleanAsync(gitDir, source, head, coreExtras, noHooks).ConfigureAwait(false);
    }

    /// <summary>The whole source checkout is wiped next, so it must hold nothing that is not in HEAD (status with exact NUL-separated output).</summary>
    private async Task VerifyCleanAsync(string gitDir, string source, string? head, string coreExtras, string noHooks)
    {
        await File.WriteAllTextAsync(Path.Combine(gitDir, "config"), MountConfig() + coreExtras, CancellationToken.None).ConfigureAwait(false);
        if (head is not null)
        {
            var readTree = await GitCommand.RunWithHooksDirAsync(gitDir, ["read-tree", "HEAD"], source, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
            if (readTree.ExitCode != 0)
            {
                throw new InvalidOperationException("Reading HEAD into a scratch index failed; plaintext kept.");
            }
        }

        var status = await GitCommand.RunWithHooksDirAsync(gitDir, ["status", "--porcelain=v1", "-z", "--ignored", "--untracked-files=all"], source, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException("git status failed on the import source; plaintext kept.");
        }

        if (status.StandardOutput.Length > 0)
        {
            throw new InvalidOperationException("The import source has changes, untracked or ignored files; commit or remove them first (the whole checkout is wiped after the import). Plaintext kept.");
        }
    }

    /// <summary>
    /// The allowlisted line-ending keys of the source config (<c>core.autocrlf</c>, <c>core.eol</c>), as config lines for the verify copy only, so
    /// a checkout made with them does not look dirty. Read with <c>git config --file</c>, which runs nothing; other values are refused.
    /// </summary>
    private async Task<string> ReadLineEndingConfigAsync(string sourceGitDir, string probeDir, string noHooks)
    {
        var sourceConfig = Path.Combine(sourceGitDir, "config");
        if (!File.Exists(sourceConfig))
        {
            return string.Empty;
        }

        // git config --file needs a repo to start in; an empty bare one runs nothing.
        var init = await GitCommand.RunWithHooksDirAsync(probeDir, ["init", "--bare", "-q"], null, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
        if (init.ExitCode != 0)
        {
            throw new InvalidOperationException("Preparing the config probe failed; nothing was imported.");
        }

        var extras = new StringBuilder();
        foreach (var (key, allowed) in new[] { ("core.autocrlf", new[] { "true", "false", "input" }), ("core.eol", new[] { "lf", "crlf", "native" }) })
        {
            var r = await GitCommand.RunWithHooksDirAsync(probeDir, ["config", "--file", sourceConfig, "--get", key], null, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
            var value = r.StandardOutput.Trim().ToLowerInvariant();
            if (r.ExitCode != 0 || value.Length == 0)
            {
                continue;
            }

            if (!allowed.Contains(value, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"The source sets {key} to a value outside the allowlist; nothing was imported.");
            }

            extras.Append('\t').Append(key["core.".Length..]).Append(" = ").Append(value).Append('\n');
        }

        return extras.ToString();
    }

    /// <summary>
    /// The commit HEAD points to, or null only for an unborn branch: HEAD is a symref whose target is in neither the loose refs nor packed-refs
    /// (of <paramref name="snapshot"/> when given, else of the folder). Any other git failure throws.
    /// </summary>
    private async Task<string?> ResolveHeadAsync(string gitDir, Dictionary<string, (long Generation, byte[] Hash)>? snapshot, string noHooks, CancellationToken ct)
    {
        var headFile = Path.Combine(gitDir, "HEAD");
        var head = File.Exists(headFile) ? (await File.ReadAllTextAsync(headFile, ct).ConfigureAwait(false)).Trim() : string.Empty;
        if (head.StartsWith("ref: ", StringComparison.Ordinal))
        {
            var target = head["ref: ".Length..].Trim();
            var loose = snapshot is not null ? snapshot.ContainsKey(target) : File.Exists(Path.Combine(gitDir, target.Replace('/', Path.DirectorySeparatorChar)));
            if (!loose && !PackedRefsContain(gitDir, target))
            {
                return null;
            }
        }

        var r = await GitCommand.RunWithHooksDirAsync(gitDir, ["rev-parse", "--verify", "HEAD^{commit}"], null, _options.GitPath, noHooks, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException($"HEAD of {Scope} does not resolve to a commit: {r.StandardError.Trim()}");
        }

        return r.StandardOutput.Trim();
    }

    private static bool PackedRefsContain(string gitDir, string refName)
    {
        var packed = Path.Combine(gitDir, "packed-refs");
        return File.Exists(packed) && File.ReadLines(packed).Any(line => line.Length > 41 && line[0] != '#' && line[0] != '^' && line[41..].Trim() == refName);
    }

    private async Task CheckoutHeadAsync(string gitDir, string checkout, Dictionary<string, (long Generation, byte[] Hash)> snapshot, CancellationToken ct)
    {
        var noHooks = Path.Combine(gitDir, _noHooksName);
        if (await ResolveHeadAsync(gitDir, snapshot, noHooks, ct).ConfigureAwait(false) is null)
        {
            return;
        }

        var r = await GitCommand.RunWithHooksDirAsync(gitDir, ["read-tree", "--reset", "-u", "HEAD"], checkout, _options.GitPath, noHooks, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException($"Checking out HEAD into the mount failed: {r.StandardError.Trim()}");
        }
    }

    /// <summary>
    /// M1: a stale mount (marker present) may hold commits made before a crash but never resealed. Its refs are compared with the sealed ones;
    /// when a ref is new or different the mount is refused and audited (<c>sealgit.stale_unsealed</c>), unless the operator asked to adopt it,
    /// in which case its git dir is resealed under the held lease with generation checks (<c>sealgit.stale_adopted</c>).
    /// </summary>
    private async Task HandleStaleCheckoutAsync(ISealedScope scope, string checkout, bool adopt)
    {
        var staleGit = Path.Combine(checkout, ".git");
        if (!File.Exists(checkout + MountMarkerSuffix) || !Directory.Exists(staleGit) || SecureTemp.IsLink(new DirectoryInfo(staleGit)))
        {
            return;
        }

        var snapshot = await SnapshotAsync(scope).ConfigureAwait(false);
        var (files, links, _) = SecureTemp.Walk(staleGit);
        var differing = new List<string>();
        foreach (var file in files)
        {
            var logical = Path.GetRelativePath(staleGit, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (!IsRefFile(logical) || !Sealable(logical))
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(file.FullName).ConfigureAwait(false);
            if (!snapshot.TryGetValue(logical, out var sealedFile) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), sealedFile.Hash))
            {
                differing.Add(logical);
            }
        }

        if (differing.Count == 0 && links.Count == 0)
        {
            return;
        }

        var detail = $"Stale mount at {checkout} has refs that are not sealed: {string.Join(", ", differing.Order(StringComparer.Ordinal).Take(20))}{(links.Count > 0 ? " (and links)" : string.Empty)}";
        if (!adopt)
        {
            AuditSafe(_audit, "sealgit.stale_unsealed", Scope, detail + "; left untouched.");
            throw new InvalidOperationException(detail + ". It was left untouched: inspect it, then set the adopt-stale-mount option to reseal it, or move it away.");
        }

        // Links make ResealCoreAsync throw before anything is written.
        await ResealCoreAsync(scope, staleGit, snapshot, MountSealable, trustImmutableObjects: false, CancellationToken.None).ConfigureAwait(false);
        AuditSafe(_audit, "sealgit.stale_adopted", Scope, detail + "; resealed by operator choice.");
    }

    private static async Task<Dictionary<string, (long Generation, byte[] Hash)>> SnapshotAsync(ISealedScope scope)
    {
        var all = await scope.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = new Dictionary<string, (long Generation, byte[] Hash)>(StringComparer.Ordinal);
        foreach (var file in all.Files)
        {
            snapshot[file.Path] = (file.Generation, SHA256.HashData(file.Content));
            CryptographicOperations.ZeroMemory(file.Content);
        }

        return snapshot;
    }

    /// <summary>The config a mount writes. Fixed and never sealed from a mount: a non-bare repo, no symlinks in the checkout, no reflogs.</summary>
    private static string MountConfig() =>
        "[core]\n\trepositoryformatversion = 0\n\tfilemode = " + (OperatingSystem.IsWindows() ? "false" : "true")
        + "\n\tbare = false\n\tlogallrefupdates = false\n\tsymlinks = false\n";

    /// <summary>M4: the mount root must have no link in any component; the result is its canonical path.</summary>
    private static string ValidateRoot(string mountRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRoot);
        var canonical = SealedPaths.Canonicalize(mountRoot);
        if (SealedPaths.HasLinkComponent(mountRoot) || !SealedPaths.PathEquals(canonical, Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot))))
        {
            throw new InvalidOperationException("The mount root must not be or sit below a link.");
        }

        return canonical;
    }

    private static (string Root, string Checkout, string? Scratch) ValidateMountRequest(SealedGitMountRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CheckoutPath);
        var root = ValidateRoot(request.MountRoot);
        var checkout = SealedPaths.Canonicalize(request.CheckoutPath);
        var scratch = request.ScratchPath is null ? null : SealedPaths.Canonicalize(request.ScratchPath);
        if (!SealedPaths.IsStrictlyInside(checkout, root))
        {
            throw new InvalidOperationException("The mount checkout must be inside the mount root.");
        }

        if (scratch is not null && (!SealedPaths.IsStrictlyInside(scratch, root) || SealedPaths.PathEquals(scratch, checkout)
            || SealedPaths.IsStrictlyInside(scratch, checkout) || SealedPaths.IsStrictlyInside(checkout, scratch)))
        {
            throw new InvalidOperationException("The mount scratch folder must be inside the mount root and outside the checkout.");
        }

        return (root, checkout, scratch);
    }

    private static void PrepareMountRoot(string root)
    {
        var info = new DirectoryInfo(root);
        if (SealedPaths.HasLinkComponent(root))
        {
            throw new InvalidOperationException("The mount root must not be or sit below a link.");
        }

        if (!info.Exists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            SecureTemp.CreateOwnerOnly(root);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode open = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(root) & open) != 0)
            {
                throw new InvalidOperationException("The mount root must be owner-only (mode 0700).");
            }
        }
    }

    /// <summary>Owner ruling 2026-11-20 (DECIDED): plaintext of a mount may live only on tmpfs or the doc 63 encrypted volume.</summary>
    private async Task RequireProtectedRootAsync(string root, IMountRootVerifier verifier)
    {
        var verdict = verifier.Verify(root);
        if (!verdict.IsProtected)
        {
            await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.mount_unverified", Scope, null, $"Mount root {root} refused: {verdict.Reason}"), CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"The mount root {root} is not confirmed as tmpfs or the encrypted volume ({verdict.Reason}); plaintext may only live there.");
        }
    }

    /// <summary>Wipes verify folders an import left in the mount root when the process died (one server per mount root).</summary>
    private void SweepImportLeftovers(string root)
    {
        foreach (var stale in Directory.EnumerateDirectories(root, _importPrefix + "*"))
        {
            if (SecureTemp.WipeAndDelete(stale) is { } failure)
            {
                AuditSafe(_audit, "sealgit.wipe_failed", Scope, $"Wipe of import leftover {stale} failed: {failure.Message}");
            }
        }
    }

    /// <summary>L6: every marker under the mount root that is not one of this mount's folders is audited (never wiped: it is not ours to judge).</summary>
    private void AuditUnknownMarkers(string root, string[] known)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var marker in Directory.EnumerateFiles(root, "*" + MountMarkerSuffix, options))
        {
            var folder = marker[..^MountMarkerSuffix.Length];
            if (!known.Any(k => SealedPaths.PathEquals(k, folder)))
            {
                AuditSafe(_audit, "sealgit.mount.unknown_marker", Scope, $"Mount marker {marker} under {root} belongs to no folder of this mount; left in place for the operator.");
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="dir"/> an empty owner-only folder that this repo owns. A folder with our marker is a stale mount and is wiped (its
    /// refs were checked first); an empty folder without one is replaced; a link or a non-empty folder without one is refused.
    /// </summary>
    private async Task ClaimMountFolderAsync(string root, string dir)
    {
        var marker = dir + MountMarkerSuffix;
        var info = new DirectoryInfo(dir);
        if (SecureTemp.IsLink(info) || SecureTemp.IsLink(new FileInfo(marker)))
        {
            throw new InvalidOperationException($"Mount folder {dir} or its marker is a link; refused.");
        }

        if (File.Exists(marker))
        {
            if (SecureTemp.WipeAndDelete(dir) is { } failure)
            {
                throw new IOException($"The stale mount at {dir} could not be wiped.", failure);
            }

            await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.mount.stale_wiped", Scope, null, $"Stale mount at {dir} wiped"), CancellationToken.None).ConfigureAwait(false);
        }
        else if (info.Exists)
        {
            if (info.EnumerateFileSystemInfos().Any())
            {
                throw new InvalidOperationException($"{dir} holds files this sealed repo did not mount; import them (ImportCheckoutAsync) or move them away. Nothing was changed.");
            }

            info.Delete();
        }

        // L7: missing folders between the root and the marker are created owner-only first.
        var missing = new Stack<string>();
        for (var parent = Path.GetDirectoryName(dir)!; !SealedPaths.PathEquals(parent, root) && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)!)
        {
            missing.Push(parent);
        }

        while (missing.Count > 0)
        {
            SecureTemp.CreateOwnerOnly(missing.Pop());
        }

        await File.WriteAllBytesAsync(marker, []).ConfigureAwait(false);
        SecureTemp.CreateOwnerOnly(dir);
    }

    /// <summary>Wipes a mount folder, then removes its marker. Returns the failure, or null; on failure the marker stays so the next mount sweeps it.</summary>
    private Exception? WipeMountFolder(string dir)
    {
        var failure = SecureTemp.WipeAndDelete(dir);
        if (failure is null)
        {
            try
            {
                File.Delete(dir + MountMarkerSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }
        }

        if (failure is not null)
        {
            AuditSafe(_audit, "sealgit.wipe_failed", Scope, $"Wipe of mount folder {dir} failed: {failure.Message}");
        }

        return failure;
    }

    private sealed class Mount : ISealedGitMount
    {
        private readonly SealedGitRepo _repo;
        private readonly ISealedScope _scope;
        private readonly ISealedLease _lease;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Dictionary<string, (long Generation, byte[] Hash)> _snapshot;
        private bool _disposed;
        private volatile bool _faulted;

        public Mount(SealedGitRepo repo, ISealedScope scope, ISealedLease lease, string path, string gitDir, string? scratch, Dictionary<string, (long Generation, byte[] Hash)> snapshot)
        {
            _repo = repo;
            _scope = scope;
            _lease = lease;
            Path = path;
            GitDir = gitDir;
            ScratchPath = scratch;
            _snapshot = snapshot;
        }

        public string Scope => _repo.Scope;

        public string Path { get; }

        public string GitDir { get; }

        public string? ScratchPath { get; }

        public bool IsFaulted => _faulted;

        public async Task ResealAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_faulted)
                {
                    throw new InvalidOperationException($"The mount of {Scope} is faulted after a failed reseal; remount to continue from the sealed state.");
                }

                try
                {
                    // Not cancellable once started, or refs and packs could be left half sealed.
                    _snapshot = await ResealCoreAsync(_scope, GitDir, _snapshot, MountSealable, trustImmutableObjects: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _faulted = true;
                    AuditSafe(_repo._audit, "sealgit.mount.reseal_failed", Scope, $"Reseal of the mount at {Path} failed: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                var failures = new List<string>();
                if (_repo.WipeMountFolder(Path) is { } checkoutFailure)
                {
                    failures.Add($"{Path}: {checkoutFailure.Message}");
                }

                if (ScratchPath is not null && _repo.WipeMountFolder(ScratchPath) is { } scratchFailure)
                {
                    failures.Add($"{ScratchPath}: {scratchFailure.Message}");
                }

                foreach (var (_, hash) in _snapshot.Values)
                {
                    CryptographicOperations.ZeroMemory(hash);
                }

                _lease.Dispose();
                _scope.Dispose();
                Volatile.Write(ref _repo._mounted, 0);
                AuditSafe(
                    _repo._audit,
                    failures.Count == 0 ? "sealgit.unmount" : "sealgit.unmount.wipe_failed",
                    Scope,
                    failures.Count == 0
                        ? $"Mount at {Path} wiped{(ScratchPath is null ? string.Empty : " with scratch " + ScratchPath)}; faulted={_faulted}"
                        : $"Mount at {Path} unmounted but not fully wiped (markers kept for the next sweep): {string.Join("; ", failures)}");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
