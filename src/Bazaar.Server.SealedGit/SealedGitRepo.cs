using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedGit;

/// <summary>Opens <see cref="SealedGitRepo"/> instances over an <see cref="ISealedStore"/>. Creating the factory wipes stale <c>bazaar-sealedgit-*</c> folders under the temp root (once per process).</summary>
public sealed class SealedGitRepoFactory : ISealedGitRepoFactory
{
    private static readonly ConcurrentDictionary<string, bool> _swept = new(StringComparer.Ordinal);

    private readonly ISealedStore _store;
    private readonly ISealAuditSink _audit;
    private readonly SealedGitOptions _options;
    private readonly Dictionary<string, SealedGitRepo> _repos = new(StringComparer.Ordinal);

    public SealedGitRepoFactory(ISealedStore store, ISealAuditSink audit, SealedGitOptions options)
    {
        _store = store;
        _audit = audit;
        _options = options;
        SweepStale(options, audit);
    }

    /// <summary>Wipes stale temp folders of crashed runs under the temp root, once per process and root. Assumes one Bazaar server per temp root.</summary>
    public static void SweepStale(SealedGitOptions options, ISealAuditSink audit)
    {
        var root = Path.GetFullPath(options.TempRoot ?? Path.GetTempPath());
        if (!_swept.TryAdd(root, true))
        {
            return;
        }

        foreach (var failure in SecureTemp.SweepStale(root))
        {
            SealedGitRepo.AuditSafe(audit, "sealgit.wipe_failed", "*", $"Stale temp sweep under {root}: {failure.Message}");
        }
    }

    public ISealedGitRepo Open(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_repos)
        {
            if (!_repos.TryGetValue(name, out var repo))
            {
                _repos[name] = repo = new SealedGitRepo("vault:" + name, _store, _audit, _options);
            }

            return repo;
        }
    }
}

/// <summary>
/// A bare git repo kept as sealed files (doc 61 Q1). Only <see cref="WithWorkingTreeAsync{T}"/> and <see cref="ExportAsync"/> ever
/// put plaintext on disk, in an owner-only temp folder that is zero-wiped afterwards. Sealed paths mirror the git dir layout.
/// The callback receives only the git dir; everything it leaves there is sealed, except <c>hooks/</c>, links, git lock and temp files,
/// and a <c>config</c> that has keys outside a small allowlist. <see cref="MountAsync"/> (doc 61 "Vault adoption") keeps a checkout under an
/// operator-chosen mount root instead; see <c>SealedGitRepo.Mount.cs</c>.
/// </summary>
public sealed partial class SealedGitRepo : ISealedGitRepo, IDisposable
{
    private const long MaxFileBytes = 256L * 1024 * 1024;

    private static readonly string[] _requiredDirs = ["refs/heads", "refs/tags", "objects/info", "objects/pack"];

    private static readonly HashSet<string> _allowedConfigKeys = new(StringComparer.Ordinal)
    {
        "core.repositoryformatversion",
        "core.filemode",
        "core.bare",
        "core.logallrefupdates",
        "user.name",
        "user.email",
    };

    private readonly ISealedStore _store;
    private readonly ISealAuditSink _audit;
    private readonly SealedGitOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _warnedTempRoot;

    public SealedGitRepo(string scope, ISealedStore store, ISealAuditSink audit, SealedGitOptions options)
    {
        Scope = scope;
        _store = store;
        _audit = audit;
        _options = options;
    }

    public string Scope { get; }

    public void Dispose() => _gate.Dispose();

    public async Task<T> WithWorkingTreeAsync<T>(Func<string, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfMounted();
            string? temp = null;
            T result;
            try
            {
                if (_options.TempRoot is null && Interlocked.Exchange(ref _warnedTempRoot, 1) == 0)
                {
                    await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.temp_root_default", Scope, null, "TempRoot is not configured; plaintext working copies use the system temp folder. Point it at tmpfs or the encrypted volume."), CancellationToken.None).ConfigureAwait(false);
                }

                temp = CreateTempDir();
                using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
                // The lease makes the whole materialise, work, reseal cycle exclusive against every other store or process working on this repo.
                using var lease = await scope.AcquireLeaseAsync(_options.LeaseTimeout, cancellationToken).ConfigureAwait(false);
                var gitDir = Path.Combine(temp, "git");
                var snapshot = await MaterialiseAsync(scope, gitDir, cancellationToken).ConfigureAwait(false);
                result = await work(gitDir).ConfigureAwait(false);

                // The work is done: finishing the re-seal is not cancellable, or refs and packs could be left half written.
                await ResealCoreAsync(scope, gitDir, snapshot, Sealable, trustImmutableObjects: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                if (temp is not null)
                {
                    await WipeAsync(temp).ConfigureAwait(false);
                }

                throw;
            }

            var failure = await WipeAsync(temp).ConfigureAwait(false);
            if (failure is not null)
            {
                throw new IOException("The plaintext working copy could not be fully wiped; see the sealgit.wipe_failed audit event.", failure);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ExportAsync(string targetDir, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var target = Path.GetFullPath(targetDir);
        try
        {
            await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.export.start", Scope, null, $"Export to {target}"), CancellationToken.None).ConfigureAwait(false);
            try
            {
                var info = new DirectoryInfo(target);
                if (SecureTemp.IsLink(info))
                {
                    throw new IOException("Export target must not be a link.");
                }

                if (info.Exists && info.EnumerateFileSystemInfos().Any())
                {
                    throw new IOException("Export target must be missing or empty.");
                }

                if (!info.Exists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    SecureTemp.CreateOwnerOnly(target);
                }

                using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
                var snapshot = await MaterialiseAsync(scope, target, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.export", Scope, null, $"{snapshot.Count} files exported in plaintext to {target}"), CancellationToken.None).ConfigureAwait(false);
                return snapshot.Count;
            }
            catch (Exception ex)
            {
                await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.export.failed", Scope, null, $"Export to {target} failed: {ex.GetType().Name}"), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static void AuditSafe(ISealAuditSink audit, string kind, string scope, string detail)
    {
        try
        {
            audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, kind, scope, null, detail), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An audit failure must not hide the original problem.
        }
    }

    private async Task<Exception?> WipeAsync(string temp)
    {
        var failure = SecureTemp.WipeAndDelete(temp);
        if (failure is not null)
        {
            try
            {
                await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.wipe_failed", Scope, null, $"Wipe of {temp} failed: {failure.Message}"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Same: do not hide the wipe failure.
            }
        }

        return failure;
    }

    private string CreateTempDir()
    {
        var root = Path.GetFullPath(_options.TempRoot ?? Path.GetTempPath());
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, SecureTemp.TempPrefix + Guid.NewGuid().ToString("N"));
        SecureTemp.CreateOwnerOnly(dir);
        return dir;
    }

    private static bool Sealable(string logicalPath)
    {
        var name = logicalPath[(logicalPath.LastIndexOf('/') + 1)..];
        return !logicalPath.StartsWith("hooks/", StringComparison.Ordinal)
            && !name.EndsWith(".lock", StringComparison.Ordinal)
            && !name.StartsWith("tmp_", StringComparison.Ordinal)
            && !name.StartsWith(".tmp", StringComparison.Ordinal)
            && name != "gc.pid";
    }

    private static async Task<Dictionary<string, (long Generation, byte[] Hash)>> MaterialiseAsync(ISealedScope scope, string gitDir, CancellationToken ct)
    {
        Directory.CreateDirectory(gitDir);
        var snapshot = new Dictionary<string, (long, byte[])>(StringComparer.Ordinal);

        // One consistent, authenticated snapshot: a concurrent writer can never give us new refs with old packs.
        var sealedFiles = await scope.ReadAllAsync(ct).ConfigureAwait(false);
        try
        {
            var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in sealedFiles.Files)
            {
                if (!folded.Add(file.Path))
                {
                    throw new InvalidOperationException($"Sealed paths collide when case is ignored ('{file.Path}'); restoring would overwrite a file on a case-insensitive file system.");
                }
            }

            foreach (var file in sealedFiles.Files)
            {
                var dest = Path.Combine(gitDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await File.WriteAllBytesAsync(dest, file.Content, ct).ConfigureAwait(false);
                snapshot[file.Path] = (file.Generation, SHA256.HashData(file.Content));
                CryptographicOperations.ZeroMemory(file.Content);
            }
        }
        finally
        {
            foreach (var file in sealedFiles.Files)
            {
                CryptographicOperations.ZeroMemory(file.Content);
            }
        }

        // gc and pack-refs can leave no files under these, so they must exist even when nothing was sealed there.
        foreach (var dir in _requiredDirs)
        {
            Directory.CreateDirectory(Path.Combine(gitDir, dir.Replace('/', Path.DirectorySeparatorChar)));
        }

        if (!snapshot.ContainsKey("HEAD"))
        {
            await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n", ct).ConfigureAwait(false);
        }

        if (!snapshot.ContainsKey("config"))
        {
            await File.WriteAllTextAsync(Path.Combine(gitDir, "config"), "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = true\n", ct).ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <summary>True when every key of the git config text is in the allowlist (no sections with names, no aliases, filters, hooks or fsmonitor).</summary>
    public static bool ConfigIsSafe(string text)
    {
        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var end = line.IndexOf(']', StringComparison.Ordinal);
                if (end < 0 || line.Contains('"', StringComparison.Ordinal) || line.Contains('.', StringComparison.Ordinal))
                {
                    return false;
                }

                section = line[1..end].Trim().ToLowerInvariant();
                var rest = line[(end + 1)..].Trim();
                if (rest.Length == 0)
                {
                    continue;
                }

                line = rest;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            var key = (eq < 0 ? line : line[..eq]).Trim().ToLowerInvariant();
            if (section.Length == 0 || !_allowedConfigKeys.Contains(section + "." + key) || line.EndsWith('\\'))
            {
                return false;
            }
        }

        return true;
    }

    private static int Tier(string path) =>
        path.StartsWith("objects/", StringComparison.Ordinal) ? 0 :
        path == "HEAD" ? 2 : 1;

    /// <summary>
    /// Seals the difference between <paramref name="gitDir"/> and <paramref name="snapshot"/> in one batch and returns the snapshot after it.
    /// <paramref name="sealable"/> decides which logical paths are sealed (and deleted when gone). With <paramref name="trustImmutableObjects"/>,
    /// content-addressed objects and packs already in the snapshot are not re-read (their names are their hashes; used by mounts).
    /// </summary>
    private static async Task<Dictionary<string, (long Generation, byte[] Hash)>> ResealCoreAsync(
        ISealedScope scope,
        string gitDir,
        Dictionary<string, (long Generation, byte[] Hash)> snapshot,
        Func<string, bool> sealable,
        bool trustImmutableObjects,
        CancellationToken ct)
    {
        var (files, links, _) = SecureTemp.Walk(gitDir);
        if (links.Count > 0)
        {
            throw new InvalidOperationException($"The working copy contains a symbolic link or reparse point ('{Path.GetRelativePath(gitDir, links[0].FullName)}'); links are never sealed. Nothing was sealed.");
        }

        // Plan everything and check every size before the first write, so a failure leaves the sealed repo untouched.
        var plan = new List<(string Logical, FileInfo File)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var logical = Path.GetRelativePath(gitDir, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (!sealable(logical))
            {
                continue;
            }

            if (trustImmutableObjects && snapshot.ContainsKey(logical) && IsImmutableObject(logical))
            {
                seen.Add(logical);
                continue;
            }

            if (file.Length > MaxFileBytes)
            {
                throw new InvalidOperationException($"'{logical}' is larger than the 256 MiB sealed file limit. Nothing was sealed.");
            }

            seen.Add(logical);
            plan.Add((logical, file));
        }

        // One batch with one manifest commit: readers see the old repo or the new one, never a mix. Every write and delete carries the
        // generation it was materialised at, so a concurrent change fails the whole batch. The order (objects and packs, then
        // packed-refs/refs/config, then HEAD, then deletions) is kept, although the single commit point already makes it all-or-nothing.
        var batch = new List<SealedWrite>();
        var buffers = new List<byte[]>();
        var newHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var (logical, file) in plan.OrderBy(p => Tier(p.Logical)).ThenBy(p => p.Logical, StringComparer.Ordinal))
            {
                var bytes = await File.ReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
                if (logical == "config" && !ConfigIsSafe(System.Text.Encoding.UTF8.GetString(bytes)))
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    continue;
                }

                var hash = SHA256.HashData(bytes);
                if (snapshot.TryGetValue(logical, out var old) && CryptographicOperations.FixedTimeEquals(hash, old.Hash))
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    continue;
                }

                buffers.Add(bytes);
                newHashes[logical] = hash;
                batch.Add(SealedWrite.Put(logical, bytes, snapshot.TryGetValue(logical, out old) ? old.Generation : 0));
            }

            var removedPaths = snapshot.Keys.Where(p => !seen.Contains(p) && sealable(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (var removed in removedPaths)
            {
                batch.Add(SealedWrite.Delete(removed, snapshot[removed].Generation));
            }

            if (batch.Count == 0)
            {
                return snapshot;
            }

            await scope.WriteBatchAsync(batch, ct).ConfigureAwait(false);

            // The generations the batch gave each file, so the next reseal of a long-lived mount checks against them.
            var after = new Dictionary<string, (long Generation, byte[] Hash)>(StringComparer.Ordinal);
            foreach (var entry in await scope.ListAsync(ct).ConfigureAwait(false))
            {
                if (newHashes.TryGetValue(entry.Path, out var written))
                {
                    after[entry.Path] = (entry.Generation, written);
                }
                else if (snapshot.TryGetValue(entry.Path, out var kept))
                {
                    after[entry.Path] = (entry.Generation, kept.Hash);
                }
            }

            return after;
        }
        finally
        {
            foreach (var bytes in buffers)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }
}
