using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// The <c>file</c> KEK provider's key file sealed by an OS keyring (doc 61 Q2). Trust chain: keyring entry (random, OS custody) seals
/// the KEK file; the KEK wraps each sealed scope's data key, including <c>host:bootstrap</c>. Nothing in the chain is sealed by a key
/// derived from itself.
/// <list type="bullet">
/// <item>Sealed file: <c>magic "BZKF" | version 1 | kind length (u8) | kind (UTF-8) | keyring payload</c>. It opens only through a
/// usable keyring of the same kind; a missing or failing keyring fails closed, never falls back.</item>
/// <item>Plain file (32 raw bytes, the protected file of earlier releases): with a usable keyring and no entry yet, it is sealed in place
/// (verify, then atomic replace; audited <c>kek.keyfile.migrated</c>). With a usable keyring that already holds the entry it is refused as
/// a downgrade. Without a usable keyring it is used with an audited warning (<c>kek.keyring.fallback</c>) unless the mode is
/// <see cref="KekKeyringMode.Required"/>.</item>
/// <item>No file: a fresh key is sealed with the keyring when usable (<c>kek.keyfile.sealed</c>), else written as the protected plain file
/// with the audited warning.</item>
/// </list>
/// The whole open runs under an exclusive lock file next to the key, so two starts never seal one key under two keyring entries.
/// </summary>
internal static class KeyringKekKeyFile
{
    private const byte _version = 1;
    private static readonly byte[] _magic = "BZKF"u8.ToArray();
    private static readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The keyring entry name for a key file: stable per path, so two installs on one host use two entries.</summary>
    public static string EntryName(string keyPath) =>
        FileKekProvider.Id + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(keyPath))))[..16];

    public static async ValueTask<byte[]> OpenOrCreateAsync(
        string keyPath,
        IKekKeyring? keyring,
        KekKeyringMode mode,
        IKekCustodyAuditSink audit,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentNullException.ThrowIfNull(audit);
        if (!Path.IsPathFullyQualified(keyPath))
            throw new ArgumentException("The KEK file path must be absolute.", nameof(keyPath));

        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new ArgumentException("The KEK file path has no directory.", nameof(keyPath));
        Directory.CreateDirectory(directory);
        using var fileLock = await AcquireLockAsync(keyPath + ".lock", cancellationToken).ConfigureAwait(false);

        var entry = EntryName(keyPath);
        var keyringKind = keyring?.Kind ?? "none";
        var probe = keyring is null
            ? KekKeyringProbe.Unavailable("no OS keyring provider on this host")
            : await keyring.ProbeAsync(entry, cancellationToken).ConfigureAwait(false);
        var aad = Aad(keyringKind, entry);

        if (File.Exists(keyPath))
        {
            var stored = await File.ReadAllBytesAsync(keyPath, cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsSealed(stored))
                {
                    if (keyring is null || !probe.Available)
                    {
                        await TryRecordAsync(audit, time, "kek.keyring.unavailable_sealed", keyringKind, "The KEK file is sealed by an OS keyring that is not usable now; the start is refused.").ConfigureAwait(false);
                        throw new KekKeyringException($"The KEK file is sealed by an OS keyring that is not usable now ({probe.Reason}). Bazaar does not fall back to another key; make the keyring available or restore from the recovery bundle (doc 48 section 6).");
                    }

                    return await OpenSealedAsync(stored, keyring, entry, aad, cancellationToken).ConfigureAwait(false);
                }

                if (stored.Length != KekKeyFile.KeySize)
                    throw new InvalidDataException("The KEK file does not hold a 32-byte key.");

                if (keyring is not null && probe.Available)
                {
                    if (probe.HasEntry)
                    {
                        await TryRecordAsync(audit, time, "kek.keyfile.downgrade_refused", keyringKind, "A plain KEK file was found while the keyring holds its entry.").ConfigureAwait(false);
                        throw new KekKeyringException(
                            $"The KEK file is plain but the {keyringKind} keyring already holds entry '{entry}', so the file may have been replaced. "
                            + "Restore the sealed KEK file from backup. Only if an interrupted migration left the plain file, and after checking it is this install's key, remove the keyring entry and restart.");
                    }

                    await WriteSealedAsync(keyPath, stored, keyring, entry, aad, overwrite: true, cancellationToken).ConfigureAwait(false);
                    await audit.RecordAsync(new KekCustodyAuditEvent(time.GetUtcNow(), "kek.keyfile.migrated", keyringKind, "The plain KEK file was sealed with the OS keyring."), cancellationToken).ConfigureAwait(false);
                    return (byte[])stored.Clone();
                }

                await FallbackAsync(mode, probe, keyringKind, audit, time, cancellationToken).ConfigureAwait(false);
                return (byte[])stored.Clone();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }

        if (keyring is not null && probe.Available)
        {
            var fresh = RandomNumberGenerator.GetBytes(KekKeyFile.KeySize);
            try
            {
                await WriteSealedAsync(keyPath, fresh, keyring, entry, aad, overwrite: false, cancellationToken).ConfigureAwait(false);
                await audit.RecordAsync(new KekCustodyAuditEvent(time.GetUtcNow(), "kek.keyfile.sealed", keyringKind, "A new KEK file was created sealed with the OS keyring."), cancellationToken).ConfigureAwait(false);
                return (byte[])fresh.Clone();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fresh);
            }
        }

        await FallbackAsync(mode, probe, keyringKind, audit, time, cancellationToken).ConfigureAwait(false);
        return KekKeyFile.OpenOrCreate(keyPath, seal: plain => (byte[])plain.Clone(), open: bytes => (byte[])bytes.Clone());
    }

    private static async ValueTask FallbackAsync(KekKeyringMode mode, KekKeyringProbe probe, string keyringKind, IKekCustodyAuditSink audit, TimeProvider time, CancellationToken cancellationToken)
    {
        if (mode == KekKeyringMode.Required)
        {
            await TryRecordAsync(audit, time, "kek.keyring.required_unavailable", keyringKind, "Bazaar:Identity:KekKeyring is 'required' and no keyring is usable; the start is refused.").ConfigureAwait(false);
            throw new KekKeyringException($"Bazaar:Identity:KekKeyring is 'required' but no OS keyring is usable ({probe.Reason}).");
        }

        // The warning must be on record before the plain file is used; a failed record fails the open.
        await audit.RecordAsync(
            new KekCustodyAuditEvent(time.GetUtcNow(), "kek.keyring.fallback", keyringKind, $"No OS keyring is usable ({probe.Reason}); the KEK stays in the protected plain file."),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteSealedAsync(string keyPath, byte[] key, IKekKeyring keyring, string entry, byte[] aad, bool overwrite, CancellationToken cancellationToken)
    {
        var payload = await keyring.SealAsync(entry, key, aad, cancellationToken).ConfigureAwait(false);
        var kind = Encoding.UTF8.GetBytes(keyring.Kind);
        if (kind.Length is 0 or > 255)
            throw new KekKeyringException("The keyring kind must be 1 to 255 UTF-8 bytes.");

        var file = new byte[_magic.Length + 2 + kind.Length + payload.Length];
        _magic.CopyTo(file, 0);
        file[4] = _version;
        file[5] = (byte)kind.Length;
        kind.CopyTo(file, 6);
        payload.CopyTo(file, 6 + kind.Length);

        // Verify before anything replaces the key: the bytes as written must open to the same key through the keyring.
        var check = await OpenSealedAsync(file, keyring, entry, aad, cancellationToken).ConfigureAwait(false);
        var same = CryptographicOperations.FixedTimeEquals(check, key);
        CryptographicOperations.ZeroMemory(check);
        if (!same)
            throw new KekKeyringException("The keyring did not return the KEK it sealed; the key file was not changed.");

        var temp = $"{keyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(file, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            var back = await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false);
            var readBack = await OpenSealedAsync(back, keyring, entry, aad, cancellationToken).ConfigureAwait(false);
            same = CryptographicOperations.FixedTimeEquals(readBack, key);
            CryptographicOperations.ZeroMemory(readBack);
            if (!same)
                throw new KekKeyringException("The sealed KEK file did not read back; the key file was not changed.");

            File.Move(temp, keyPath, overwrite);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static async ValueTask<byte[]> OpenSealedAsync(byte[] stored, IKekKeyring keyring, string entry, byte[] aad, CancellationToken cancellationToken)
    {
        if (stored.Length < 6 || stored[4] != _version || stored.Length < 6 + stored[5])
            throw new KekKeyringException("The sealed KEK file is malformed.");

        var kind = Encoding.UTF8.GetString(stored, 6, stored[5]);
        if (!string.Equals(kind, keyring.Kind, StringComparison.Ordinal))
            throw new KekKeyringException($"The KEK file is sealed by keyring '{kind}', not by this host's '{keyring.Kind}'.");

        var key = await keyring.OpenAsync(entry, stored.AsMemory(6 + stored[5]), aad, cancellationToken).ConfigureAwait(false);
        if (key.Length != KekKeyFile.KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new KekKeyringException("The sealed KEK file does not hold a 32-byte key.");
        }

        return key;
    }

    // A plain key is exactly 32 bytes; a sealed file is always longer, so a random key that starts with the magic is still plain.
    private static bool IsSealed(byte[] stored) => stored.Length > KekKeyFile.KeySize && stored.AsSpan(0, _magic.Length).SequenceEqual(_magic);

    private static byte[] Aad(string kind, string entry) => Encoding.UTF8.GetBytes($"bazaar.kek.file.v1|{kind}|{entry}|{FileKekProvider.Id}");

    private static async ValueTask TryRecordAsync(IKekCustodyAuditSink audit, TimeProvider time, string kind, string keyringKind, string detail)
    {
        try
        {
            // Never the caller's token: a refusal must still leave a record.
            await audit.RecordAsync(new KekCustodyAuditEvent(time.GetUtcNow(), kind, keyringKind, detail), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failing sink must not turn a refusal into something else: the caller still gets the refusal.
        }
    }

    private static async ValueTask<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _lockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
