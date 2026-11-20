using System.Security.Cryptography;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedGit;

/// <summary>Opens <see cref="SealedGitRepo"/> instances over an <see cref="ISealedStore"/>.</summary>
public sealed class SealedGitRepoFactory : ISealedGitRepoFactory
{
    private readonly ISealedStore _store;
    private readonly ISealAuditSink _audit;
    private readonly SealedGitOptions _options;
    private readonly Dictionary<string, SealedGitRepo> _repos = new(StringComparer.Ordinal);

    public SealedGitRepoFactory(ISealedStore store, ISealAuditSink audit, SealedGitOptions options)
    {
        _store = store;
        _audit = audit;
        _options = options;
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
/// put plaintext on disk, in a temp folder that is zero-wiped afterwards. Sealed paths mirror the git dir layout.
/// </summary>
public sealed class SealedGitRepo : ISealedGitRepo, IDisposable
{
    private readonly ISealedStore _store;
    private readonly ISealAuditSink _audit;
    private readonly SealedGitOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        var temp = CreateTempDir();
        try
        {
            using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
            var gitDir = Path.Combine(temp, "git");
            var snapshot = await MaterialiseAsync(scope, gitDir, cancellationToken).ConfigureAwait(false);
            var result = await work(gitDir).ConfigureAwait(false);
            await ResealAsync(scope, gitDir, snapshot, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            WipeAndDelete(temp);
            _gate.Release();
        }
    }

    public async Task<int> ExportAsync(string targetDir, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
        {
            throw new IOException("Export target must be missing or empty.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
            var snapshot = await MaterialiseAsync(scope, targetDir, cancellationToken).ConfigureAwait(false);
            await _audit.RecordAsync(new SealAuditEvent(DateTimeOffset.UtcNow, "sealgit.export", Scope, null, $"{snapshot.Count} files exported in plaintext"), cancellationToken).ConfigureAwait(false);
            return snapshot.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CreateTempDir()
    {
        var root = _options.TempRoot ?? Path.GetTempPath();
        var dir = Path.Combine(root, "bazaar-sealedgit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }

    private static async Task<Dictionary<string, (long Generation, byte[] Hash)>> MaterialiseAsync(ISealedScope scope, string gitDir, CancellationToken ct)
    {
        Directory.CreateDirectory(gitDir);
        var snapshot = new Dictionary<string, (long, byte[])>(StringComparer.Ordinal);
        var entries = await scope.ListAsync(ct).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            var file = await scope.ReadAsync(entry.Path, ct).ConfigureAwait(false)
                ?? throw new SealTamperedException(scope.Scope, entry.Path, "Listed file vanished while materialising.");
            var dest = Path.Combine(gitDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, file.Content, ct).ConfigureAwait(false);
            snapshot[entry.Path] = (file.Generation, SHA256.HashData(file.Content));
            CryptographicOperations.ZeroMemory(file.Content);
        }

        if (entries.Count == 0)
        {
            Directory.CreateDirectory(Path.Combine(gitDir, "objects", "pack"));
            Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
            Directory.CreateDirectory(Path.Combine(gitDir, "refs", "tags"));
            await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n", ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(gitDir, "config"), "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = true\n", ct).ConfigureAwait(false);
        }

        return snapshot;
    }

    private static bool IsTransient(string logicalPath)
    {
        var name = logicalPath[(logicalPath.LastIndexOf('/') + 1)..];
        return name.EndsWith(".lock", StringComparison.Ordinal)
            || name.StartsWith("tmp_", StringComparison.Ordinal)
            || name.StartsWith(".tmp", StringComparison.Ordinal);
    }

    private static async Task ResealAsync(ISealedScope scope, string gitDir, Dictionary<string, (long Generation, byte[] Hash)> snapshot, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories))
        {
            var logical = Path.GetRelativePath(gitDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (IsTransient(logical))
            {
                continue;
            }

            seen.Add(logical);
            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            try
            {
                if (snapshot.TryGetValue(logical, out var old))
                {
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), old.Hash))
                    {
                        await scope.WriteAsync(logical, bytes, old.Generation, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await scope.WriteAsync(logical, bytes, 0, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        foreach (var removed in snapshot.Keys.Where(p => !seen.Contains(p)))
        {
            await scope.DeleteAsync(removed, ct).ConfigureAwait(false);
        }
    }

    internal static void WipeAndDelete(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                var length = new FileInfo(file).Length;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None);
                var zeros = new byte[(int)Math.Min(Math.Max(length, 1), 81920)];
                for (long done = 0; done < length; done += zeros.Length)
                {
                    fs.Write(zeros, 0, (int)Math.Min(zeros.Length, length - done));
                }

                fs.Flush(true);
            }
            catch (IOException)
            {
                // Best effort: the delete below still removes the name.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        Directory.Delete(dir, recursive: true);
    }
}
