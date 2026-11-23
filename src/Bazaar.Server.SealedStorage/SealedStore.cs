using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.SealedStorage;

/// <summary>
/// The sealed store (doc 61): one folder per scope under <see cref="SealedStoreOptions.RootPath"/>, holding a KEK-wrapped data key,
/// an authenticated manifest and AES-256-GCM files. Failures to authenticate, missing files and rollbacks are audited and thrown as
/// <see cref="SealTamperedException"/>; nothing falls back to a plaintext or unverified path.
/// </summary>
public sealed class SealedStore : ISealedStore
{
    private readonly string _root;
    private readonly IKekProvider _kek;
    private readonly ISealAnchor _anchor;
    private readonly ISealAuditSink _audit;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public SealedStore(SealedStoreOptions options, IKekProvider kek, ISealAnchor anchor, ISealAuditSink audit, TimeProvider? time = null)
    {
        _root = options.RootPath;
        _kek = kek;
        _anchor = anchor;
        _audit = audit;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<ISealedScope> OpenScopeAsync(string scope, CancellationToken cancellationToken = default)
    {
        scope = SealFormat.NormaliseScope(scope);
        var directory = Path.Combine(_root, Convert.ToHexString(SHA256.HashData(SealFormat.StrictUtf8.GetBytes(scope)))[..32]);
        var gate = GateFor(scope);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var keyPath = Path.Combine(directory, "scope.key");
            byte[] dek;
            if (File.Exists(keyPath))
            {
                dek = await UnwrapAsync(scope, keyPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (Directory.Exists(directory))
                {
                    // Leftovers of a crashed creation (temp files, the lock file) are not data.
                    foreach (var leftover in Directory.EnumerateFiles(directory, "*.tmp"))
                    {
                        TryDeleteQuietly(leftover);
                    }
                }

                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(e => Path.GetFileName(e) != "scope.lock"))
                {
                    throw await FailAsync(scope, null, "Scope key header is missing but the scope folder is not empty.", cancellationToken).ConfigureAwait(false);
                }

                if (await _anchor.GetAsync(scope, cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw await FailAsync(scope, null, "Scope folder is gone but its anchor remembers it.", cancellationToken).ConfigureAwait(false);
                }

                dek = await CreateKeyAsync(scope, directory, keyPath, cancellationToken).ConfigureAwait(false);
            }

            var opened = new SealedScope(this, scope, directory, dek, gate);
            try
            {
                await opened.InitialiseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                opened.ZeroKeys();
                throw;
            }

            return opened;
        }
        finally
        {
            gate.Release();
        }
    }

    internal ISealAnchor Anchor => _anchor;

    internal async ValueTask<SealTamperedException> FailAsync(string scope, string? path, string detail, CancellationToken cancellationToken)
    {
        try
        {
            // Never the caller's token: a cancelled caller must still leave a tamper record.
            await _audit.RecordAsync(new SealAuditEvent(_time.GetUtcNow(), "seal.tamper", scope, path, detail), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failing audit sink must not hide the tamper: the caller still gets the exception and no data.
        }

        return new SealTamperedException(scope, path, detail);
    }

    internal async ValueTask RecordAsync(string kind, string scope, string? path, string detail, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(new SealAuditEvent(_time.GetUtcNow(), kind, scope, path, detail), cancellationToken).ConfigureAwait(false);
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private SemaphoreSlim GateFor(string scope)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(scope, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[scope] = gate;
            }

            return gate;
        }
    }

    private async ValueTask<byte[]> CreateKeyAsync(string scope, string directory, string keyPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var dek = RandomNumberGenerator.GetBytes(SealFormat.KeyBytes);
        try
        {
            var kekId = _kek.KekId;
            if (Encoding.UTF8.GetByteCount(kekId) is 0 or > 255)
            {
                throw new InvalidOperationException("The KEK id must be 1 to 255 UTF-8 bytes.");
            }

            var wrapped = await _kek.WrapAsync(dek, SealFormat.DekAad(scope, kekId), cancellationToken).ConfigureAwait(false);
            var idBytes = Encoding.UTF8.GetBytes(kekId);
            using var stream = new MemoryStream();
            stream.Write(SealFormat.KeyMagic);
            stream.WriteByte(SealFormat.Version);
            stream.WriteByte((byte)idBytes.Length);
            stream.Write(idBytes);
            stream.Write(wrapped);
            var temp = $"{keyPath}.{Guid.NewGuid():N}.tmp";
            await SealFormat.WriteDurableAsync(temp, stream.ToArray(), cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temp, keyPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                // Another instance created the scope first: use its key, never ours.
                TryDeleteQuietly(temp);
                CryptographicOperations.ZeroMemory(dek);
                return await UnwrapAsync(scope, keyPath, cancellationToken).ConfigureAwait(false);
            }

            return dek;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dek);
            throw;
        }
    }

    private async ValueTask<byte[]> UnwrapAsync(string scope, string keyPath, CancellationToken cancellationToken)
    {
        byte[]? bytes;
        try
        {
            bytes = await SealFormat.ReadBoundedAsync(keyPath, 4096, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bytes = null;
        }

        if (bytes is null || bytes.Length < 6 || !bytes.AsSpan(0, 4).SequenceEqual(SealFormat.KeyMagic) || bytes[4] != SealFormat.Version || bytes.Length < 6 + bytes[5])
        {
            throw await FailAsync(scope, null, "Scope key header is malformed.", cancellationToken).ConfigureAwait(false);
        }

        var kekId = Encoding.UTF8.GetString(bytes, 6, bytes[5]);
        var wrapped = bytes.AsMemory(6 + bytes[5]);
        if (!string.Equals(kekId, _kek.KekId, StringComparison.Ordinal))
        {
            throw await FailAsync(scope, null, $"Scope key was wrapped under KEK '{kekId}', not the active KEK.", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var dek = await _kek.UnwrapAsync(wrapped, SealFormat.DekAad(scope, kekId), cancellationToken).ConfigureAwait(false);
            if (dek.Length == SealFormat.KeyBytes)
            {
                return dek;
            }

            CryptographicOperations.ZeroMemory(dek);
            throw await FailAsync(scope, null, "Scope key has the wrong length.", cancellationToken).ConfigureAwait(false);
        }
        catch (KekUnwrapException)
        {
            throw await FailAsync(scope, null, "Scope key header does not authenticate under the KEK.", cancellationToken).ConfigureAwait(false);
        }
    }
}
