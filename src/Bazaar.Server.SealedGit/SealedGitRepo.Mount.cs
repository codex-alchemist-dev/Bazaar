using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedGit;

/// <summary>
/// The long-lived mount and the plaintext import (doc 61 "Vault adoption", PROPOSED). A mount keeps the git dir and a checkout of HEAD under an
/// operator-chosen mount root for as long as the engine needs them, holds the scope lease for that whole time, and reseals the git dir after
/// each mutating operation through the same batch path as <see cref="WithWorkingTreeAsync{T}"/>.
/// </summary>
public sealed partial class SealedGitRepo
{
    /// <summary>Suffix of the marker file next to a mounted folder. A folder with a marker is ours: a stale one is wiped, never refused.</summary>
    internal const string MountMarkerSuffix = ".bazaar-sealed-mount";

    private const string _noHooksName = "bazaar-nohooks";

    /// <summary>Git-dir paths a mount never seals: per-checkout state the next mount rebuilds, and anything that is configuration.</summary>
    private static readonly HashSet<string> _mountLocalFiles = new(StringComparer.Ordinal)
    {
        "config", "index", "description", "COMMIT_EDITMSG", "ORIG_HEAD", "FETCH_HEAD", "AUTO_MERGE",
        "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "MERGE_RR", "CHERRY_PICK_HEAD", "REVERT_HEAD",
    };

    private static readonly string[] _mountLocalPrefixes = ["worktrees/", "info/", "logs/", _noHooksName + "/", "bazaar-"];

    private int _mounted;

    [GeneratedRegex(@"^objects/(?:[0-9a-f]{2}/[0-9a-f]{38,62}|pack/pack-[0-9a-f]{40,64}\.(?:pack|idx|rev|bitmap))$", RegexOptions.CultureInvariant)]
    private static partial Regex ImmutableObject();

    private static bool IsImmutableObject(string logical) => ImmutableObject().IsMatch(logical);

    /// <summary>What a mount (and an import) seals: <see cref="Sealable"/> minus per-checkout state, config, reflogs and worktree metadata.</summary>
    internal static bool MountSealable(string logical) =>
        Sealable(logical)
        && !_mountLocalFiles.Contains(logical)
        && !_mountLocalPrefixes.Any(p => logical.StartsWith(p, StringComparison.Ordinal));

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
        var (root, checkout, scratch) = ValidateMountRequest(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfMounted();
            PrepareMountRoot(root);

            var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
            ISealedLease? lease = null;
            var owned = new List<string>();
            try
            {
                lease = await scope.AcquireLeaseAsync(_options.LeaseTimeout, cancellationToken).ConfigureAwait(false);
                var folders = scratch is null ? new[] { checkout } : [checkout, scratch];
                foreach (var dir in folders)
                {
                    await ClaimMountFolderAsync(dir).ConfigureAwait(false);
                    owned.Add(dir);
                }

                var gitDir = Path.Combine(checkout, ".git");
                var snapshot = await MaterialiseAsync(scope, gitDir, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(gitDir, "config"), MountConfig(), cancellationToken).ConfigureAwait(false);
                await CheckoutHeadAsync(gitDir, checkout, cancellationToken).ConfigureAwait(false);

                await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.mount", Scope, null, $"Mounted in plaintext at {checkout} under mount root {root}"), CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _mounted, 1);
                return new Mount(this, scope, lease, checkout, gitDir, scratch, snapshot);
            }
            catch (Exception ex)
            {
                // Only folders this call claimed are wiped; a folder with plaintext we did not mount is left for the operator.
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

    public async Task<int> ImportCheckoutAsync(string checkoutPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutPath);
        var source = Path.GetFullPath(checkoutPath);
        var gitDir = Path.Combine(source, ".git");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfMounted();
            await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.import.start", Scope, null, $"Import of {source}"), CancellationToken.None).ConfigureAwait(false);
            try
            {
                var count = await ImportCoreAsync(source, gitDir, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.import", Scope, null, $"{count} git files sealed and verified; plaintext checkout {source} wiped"), CancellationToken.None).ConfigureAwait(false);
                return count;
            }
            catch (Exception ex)
            {
                AuditSafe(_audit, "sealgit.import.failed", Scope, $"Import of {source} failed: {ex.GetType().Name}");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ImportCoreAsync(string source, string gitDir, CancellationToken ct)
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

        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
        {
            throw new InvalidOperationException("The source has a pending merge; finish or abort it first.");
        }

        var worktrees = Path.Combine(gitDir, "worktrees");
        if (Directory.Exists(worktrees) && Directory.EnumerateFileSystemEntries(worktrees).Any())
        {
            throw new InvalidOperationException("The source has worktrees; remove and prune them first.");
        }

        var temp = CreateTempDir();
        try
        {
            var noHooks = Path.Combine(temp, "nohooks");
            var head = await RevParseHeadAsync(gitDir, null, noHooks, ct).ConfigureAwait(false);

            using var scope = await _store.OpenScopeAsync(Scope, ct).ConfigureAwait(false);
            using var lease = await scope.AcquireLeaseAsync(_options.LeaseTimeout, ct).ConfigureAwait(false);
            if ((await scope.ListAsync(ct).ConfigureAwait(false)).Count > 0)
            {
                throw new InvalidOperationException($"Sealed repo {Scope} is not empty; an import only goes into an empty repo.");
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
                    // Once the batch is committed the plaintext is still the only copy we trust until verification passes, so this is not cancellable.
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
                await VerifyImportAsync(scope, hashes, head, source, Path.Combine(temp, "verify"), noHooks).ConfigureAwait(false);
            }
            catch
            {
                // Roll the sealed copy back so the operator can retry; the plaintext was never touched.
                try
                {
                    await scope.WriteBatchAsync(hashes.Keys.Select(p => SealedWrite.Delete(p)).ToList(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AuditSafe(_audit, "sealgit.import.rollback_failed", Scope, $"Rollback after a failed verification failed: {ex.GetType().Name}");
                }

                throw;
            }

            var failure = SecureTemp.WipeAndDelete(source);
            if (failure is not null)
            {
                AuditSafe(_audit, "sealgit.wipe_failed", Scope, $"Wipe of the imported plaintext {source} failed: {failure.Message}");
                throw new IOException("The repo was sealed and verified, but the plaintext checkout could not be fully wiped; see the sealgit.wipe_failed audit event.", failure);
            }

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

    /// <summary>
    /// Checks the sealed copy against the source: same files and bytes, same HEAD, <c>git fsck</c> clean, and the source checkout has no change,
    /// untracked or ignored file. Every git call here runs on the restored copy, whose config is ours, so the source's config (filters,
    /// fsmonitor, aliases) is never used.
    /// </summary>
    private async Task VerifyImportAsync(ISealedScope scope, Dictionary<string, byte[]> expected, string? head, string source, string restoreDir, string noHooks)
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

        var gitDir = Path.Combine(restoreDir, "git");
        await MaterialiseAsync(scope, gitDir, CancellationToken.None).ConfigureAwait(false);
        if (await RevParseHeadAsync(gitDir, null, noHooks, CancellationToken.None).ConfigureAwait(false) != head)
        {
            throw new InvalidOperationException("HEAD of the restored sealed repo differs from the source; plaintext kept.");
        }

        var fsck = await GitCommand.RunWithHooksDirAsync(gitDir, ["fsck", "--no-progress", "--no-dangling"], null, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
        if (fsck.ExitCode != 0)
        {
            throw new InvalidOperationException("git fsck failed on the restored sealed repo; plaintext kept.");
        }

        // The whole source checkout is wiped next, so it must hold nothing that is not in HEAD.
        await File.WriteAllTextAsync(Path.Combine(gitDir, "config"), MountConfig(), CancellationToken.None).ConfigureAwait(false);
        if (head is not null)
        {
            var readTree = await GitCommand.RunWithHooksDirAsync(gitDir, ["read-tree", "HEAD"], source, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
            if (readTree.ExitCode != 0)
            {
                throw new InvalidOperationException("Reading HEAD into a scratch index failed; plaintext kept.");
            }
        }

        var status = await GitCommand.RunWithHooksDirAsync(gitDir, ["status", "--porcelain=v1", "--ignored", "--untracked-files=all"], source, _options.GitPath, noHooks, CancellationToken.None).ConfigureAwait(false);
        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException("git status failed on the import source; plaintext kept.");
        }

        var dirty = status.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.EndsWith(" .git", StringComparison.Ordinal) && !line.EndsWith(" .git/", StringComparison.Ordinal))
            .ToList();
        if (dirty.Count > 0)
        {
            throw new InvalidOperationException("The import source has changes, untracked or ignored files; commit or remove them first (the whole checkout is wiped after the import). Plaintext kept.");
        }
    }

    /// <summary>The commit HEAD points to, or null for an unborn branch.</summary>
    private async Task<string?> RevParseHeadAsync(string gitDir, string? workTree, string noHooks, CancellationToken ct)
    {
        var r = await GitCommand.RunWithHooksDirAsync(gitDir, ["rev-parse", "--verify", "-q", "HEAD^{commit}"], workTree, _options.GitPath, noHooks, ct).ConfigureAwait(false);
        return r.ExitCode == 0 ? r.StandardOutput.Trim() : null;
    }

    private async Task CheckoutHeadAsync(string gitDir, string checkout, CancellationToken ct)
    {
        var noHooks = Path.Combine(gitDir, _noHooksName);
        if (await RevParseHeadAsync(gitDir, checkout, noHooks, ct).ConfigureAwait(false) is null)
        {
            return;
        }

        var r = await GitCommand.RunWithHooksDirAsync(gitDir, ["read-tree", "--reset", "-u", "HEAD"], checkout, _options.GitPath, noHooks, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException("Checking out HEAD into the mount failed.");
        }
    }

    /// <summary>The config a mount writes. Fixed and never sealed from a mount: a non-bare repo, no symlinks in the checkout, no reflogs.</summary>
    private static string MountConfig() =>
        "[core]\n\trepositoryformatversion = 0\n\tfilemode = " + (OperatingSystem.IsWindows() ? "false" : "true")
        + "\n\tbare = false\n\tlogallrefupdates = false\n\tsymlinks = false\n";

    private static (string Root, string Checkout, string? Scratch) ValidateMountRequest(SealedGitMountRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MountRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CheckoutPath);
        var root = Trim(Path.GetFullPath(request.MountRoot));
        var checkout = Trim(Path.GetFullPath(request.CheckoutPath));
        var scratch = request.ScratchPath is null ? null : Trim(Path.GetFullPath(request.ScratchPath));
        if (!IsStrictlyInside(checkout, root))
        {
            throw new InvalidOperationException("The mount checkout must be inside the mount root.");
        }

        if (scratch is not null && (!IsStrictlyInside(scratch, root) || scratch == checkout || IsStrictlyInside(scratch, checkout) || IsStrictlyInside(checkout, scratch)))
        {
            throw new InvalidOperationException("The mount scratch folder must be inside the mount root and outside the checkout.");
        }

        return (root, checkout, scratch);
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);

    /// <summary>True when <paramref name="path"/> is below <paramref name="parent"/> (both full paths). Case-insensitive on Windows and macOS.</summary>
    internal static bool IsStrictlyInside(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Trim(parent) + Path.DirectorySeparatorChar;
        return Trim(path).StartsWith(prefix, comparison) && Trim(path).Length > prefix.Length;
    }

    private static void PrepareMountRoot(string root)
    {
        var info = new DirectoryInfo(root);
        if (SecureTemp.IsLink(info))
        {
            throw new InvalidOperationException("The mount root must not be a link.");
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

    /// <summary>
    /// Makes <paramref name="dir"/> an empty owner-only folder that this repo owns. A folder with our marker is a stale mount and is wiped;
    /// an empty folder without one is replaced; a link or a non-empty folder without one is refused.
    /// </summary>
    private async Task ClaimMountFolderAsync(string dir)
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
                _repo.WipeMountFolder(Path);
                if (ScratchPath is not null)
                {
                    _repo.WipeMountFolder(ScratchPath);
                }

                foreach (var (_, hash) in _snapshot.Values)
                {
                    CryptographicOperations.ZeroMemory(hash);
                }

                _lease.Dispose();
                _scope.Dispose();
                Volatile.Write(ref _repo._mounted, 0);
                AuditSafe(_repo._audit, "sealgit.unmount", Scope, $"Mount at {Path} wiped");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
