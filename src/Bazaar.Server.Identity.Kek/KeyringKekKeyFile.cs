using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// The <c>file</c> KEK provider's key file sealed by an OS keyring (doc 61 Q2). Trust chain: keyring entry (random, OS custody) seals
/// the KEK file; the KEK wraps each sealed scope's data key, including <c>host:bootstrap</c>. Nothing in the chain is sealed by a key
/// derived from itself.
/// <list type="bullet">
/// <item>Sealed file: <c>magic "BZKF" | version 1 | kind length (u8) | kind (UTF-8) | entry length (u8) | entry (UTF-8) | keyring payload</c>.
/// The entry name comes from this header, not from the file's path, so a moved data root still finds its entry; kind and entry are bound
/// into the payload's associated data. It opens only through a usable keyring of the same kind; a missing or failing keyring fails
/// closed, never falls back.</item>
/// <item>Sticky marker <c>kek.sealed</c> next to the key, written after the first successful seal. While it exists a plain key file (or
/// a missing one) is refused whatever the keyring says, on every OS, so deleting or swapping the sealed file cannot downgrade the install.</item>
/// <item>Plain file (32 raw bytes, the protected file of earlier releases): with a usable keyring and no entry yet, it is sealed in place
/// (the plain key is first checked against any existing wrapped DEK header through <see cref="IKekWrapVerifier"/>, a copy is kept as
/// <c>kek.bin.premigrate</c> until the sealed key is verified the same way, then verify and atomic replace; audited
/// <c>kek.keyfile.migrated</c>). With a usable keyring that already holds the entry it is refused as a downgrade unless the keyring's key
/// commitment matches this key, which proves an interrupted migration of this very key. Without a usable keyring and without the marker it
/// is used with an audited warning (<c>kek.keyring.fallback</c>) unless the mode is <see cref="KekKeyringMode.Required"/>. A 32-byte file
/// that starts like a sealed file (a truncated one) is refused.</item>
/// <item>No file: a fresh key is sealed with the keyring when usable (<c>kek.keyfile.sealed</c>), else written as the protected plain file
/// with the audited warning.</item>
/// </list>
/// The whole open runs under an exclusive lock file next to the key, so two starts never seal one key under two keyring entries; a lock that
/// cannot be taken in time is audited (<c>kek.keyfile.lock_timeout</c>) and refused.
/// </summary>
internal static class KeyringKekKeyFile
{
    public const string MarkerFileName = "kek.sealed";
    public const string PremigrateSuffix = ".premigrate";

    private const byte _version = 1;
    private const int _fixedHeader = 4 + 1 + 1;
    private static readonly byte[] _magic = "BZKF"u8.ToArray();
    private static readonly TimeSpan _defaultLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The keyring entry name given to a new key file: stable per path, so two installs on one host use two entries. An existing sealed file names its own entry in its header.</summary>
    public static string EntryName(string keyPath) =>
        FileKekProvider.Id + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(keyPath))))[..16];

    public static async ValueTask<byte[]> OpenOrCreateAsync(
        string keyPath,
        IKekKeyring? keyring,
        KekKeyringMode mode,
        IKekCustodyAuditSink audit,
        TimeProvider time,
        IKekWrapVerifier? wrapVerifier,
        TimeSpan? lockTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentNullException.ThrowIfNull(audit);
        if (!Path.IsPathFullyQualified(keyPath))
            throw new ArgumentException("The KEK file path must be absolute.", nameof(keyPath));

        var directory = Path.GetDirectoryName(keyPath)
            ?? throw new ArgumentException("The KEK file path has no directory.", nameof(keyPath));
        KekKeyFile.CreateKeyDirectory(directory);
        var keyringKind = keyring?.Kind ?? "none";
        using var fileLock = await AcquireLockAsync(keyPath + ".lock", lockTimeout ?? _defaultLockTimeout, audit, time, keyringKind, cancellationToken).ConfigureAwait(false);

        var markerPath = Path.Combine(directory, MarkerFileName);
        var marked = File.Exists(markerPath);

        if (File.Exists(keyPath))
        {
            var stored = await File.ReadAllBytesAsync(keyPath, cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsSealed(stored))
                {
                    return await OpenSealedFileAsync(stored, keyPath, markerPath, keyring, keyringKind, audit, time, wrapVerifier, cancellationToken).ConfigureAwait(false);
                }

                if (stored.Length == KekKeyFile.KeySize && stored.AsSpan(0, _magic.Length).SequenceEqual(_magic) && stored[_magic.Length] == _version)
                {
                    // A random 32-byte key starts with the magic and version with probability 2^-40: this is a sealed file cut down to key size.
                    await TryRecordAsync(audit, time, "kek.keyfile.truncated_sealed", keyringKind, "A 32-byte KEK file starts like a sealed file; it is refused.").ConfigureAwait(false);
                    throw new KekKeyringException("The KEK file is 32 bytes but starts with the sealed-file marker, so it looks like a truncated sealed file. Restore the sealed KEK file from backup.");
                }

                if (stored.Length != KekKeyFile.KeySize)
                    throw new InvalidDataException("The KEK file does not hold a 32-byte key.");

                if (marked)
                {
                    await TryRecordAsync(audit, time, "kek.keyfile.downgrade_refused", keyringKind, "A plain KEK file was found but this install was sealed before (marker present).").ConfigureAwait(false);
                    throw new KekKeyringException(
                        $"The KEK file is plain but this install was sealed before ({MarkerFileName} exists), so the file may have been replaced. Restore the sealed KEK file from backup.");
                }

                var entry = EntryName(keyPath);
                var probe = keyring is null
                    ? KekKeyringProbe.Unavailable("no OS keyring provider on this host")
                    : await keyring.ProbeAsync(entry, cancellationToken).ConfigureAwait(false);
                if (keyring is not null && probe.Available)
                {
                    var resumed = false;
                    if (probe.HasEntry)
                    {
                        if (!await keyring.VerifyCommitmentAsync(entry, stored, cancellationToken).ConfigureAwait(false))
                        {
                            await TryRecordAsync(audit, time, "kek.keyfile.downgrade_refused", keyringKind, "A plain KEK file was found while the keyring holds its entry and the key commitment does not match.").ConfigureAwait(false);
                            throw new KekKeyringException(
                                $"The KEK file is plain but the {keyringKind} keyring already holds entry '{entry}' and its key commitment does not match this file, so the file may have been replaced. "
                                + "Restore the sealed KEK file from backup. Only if you can show the plain file is this install's key, remove the keyring entry and restart.");
                        }

                        resumed = true;
                    }

                    await MigrateAsync(stored, keyPath, markerPath, keyring, keyringKind, entry, resumed, audit, time, wrapVerifier, cancellationToken).ConfigureAwait(false);
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

        if (marked)
        {
            await TryRecordAsync(audit, time, "kek.keyfile.missing_sealed", keyringKind, "The KEK file is missing but this install was sealed before (marker present); no new key is made.").ConfigureAwait(false);
            throw new KekKeyringException(
                $"The KEK file is missing but this install was sealed before ({MarkerFileName} exists). A new key would orphan every wrapped data key; restore the sealed KEK file from backup.");
        }

        var freshEntry = EntryName(keyPath);
        var freshProbe = keyring is null
            ? KekKeyringProbe.Unavailable("no OS keyring provider on this host")
            : await keyring.ProbeAsync(freshEntry, cancellationToken).ConfigureAwait(false);
        if (keyring is not null && freshProbe.Available)
        {
            var fresh = RandomNumberGenerator.GetBytes(KekKeyFile.KeySize);
            try
            {
                await WriteSealedAsync(keyPath, fresh, keyring, freshEntry, overwrite: false, cancellationToken).ConfigureAwait(false);
                await WriteMarkerAsync(markerPath, keyringKind, freshEntry).ConfigureAwait(false);
                await audit.RecordAsync(new KekCustodyAuditEvent(time.GetUtcNow(), "kek.keyfile.sealed", keyringKind, "A new KEK file was created sealed with the OS keyring."), cancellationToken).ConfigureAwait(false);
                return (byte[])fresh.Clone();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fresh);
            }
        }

        await FallbackAsync(mode, freshProbe, keyringKind, audit, time, cancellationToken).ConfigureAwait(false);
        return KekKeyFile.OpenOrCreate(keyPath, seal: plain => (byte[])plain.Clone(), open: bytes => (byte[])bytes.Clone());
    }

    private static async ValueTask<byte[]> OpenSealedFileAsync(
        byte[] stored,
        string keyPath,
        string markerPath,
        IKekKeyring? keyring,
        string keyringKind,
        IKekCustodyAuditSink audit,
        TimeProvider time,
        IKekWrapVerifier? wrapVerifier,
        CancellationToken cancellationToken)
    {
        var header = ParseHeader(stored);
        var probe = keyring is null
            ? KekKeyringProbe.Unavailable("no OS keyring provider on this host")
            : await keyring.ProbeAsync(header.Entry, cancellationToken).ConfigureAwait(false);
        if (keyring is null || !probe.Available)
        {
            await TryRecordAsync(audit, time, "kek.keyring.unavailable_sealed", keyringKind, "The KEK file is sealed by an OS keyring that is not usable now; the start is refused.").ConfigureAwait(false);
            throw new KekKeyringException($"The KEK file is sealed by an OS keyring that is not usable now ({probe.Reason}). Bazaar does not fall back to another key; make the keyring available or restore from the recovery bundle (doc 48 section 6).");
        }

        var key = await OpenSealedAsync(stored, keyring, cancellationToken).ConfigureAwait(false);
        try
        {
            // A crash between the rename and the marker leaves a sealed file without one; the first clean open completes it.
            if (!File.Exists(markerPath))
                await WriteMarkerAsync(markerPath, header.Kind, header.Entry).ConfigureAwait(false);

            await FinishPremigrateAsync(keyPath, key, audit, time, keyringKind, wrapVerifier, cancellationToken).ConfigureAwait(false);
            return (byte[])key.Clone();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async ValueTask MigrateAsync(
        byte[] plain,
        string keyPath,
        string markerPath,
        IKekKeyring keyring,
        string keyringKind,
        string entry,
        bool resumed,
        IKekCustodyAuditSink audit,
        TimeProvider time,
        IKekWrapVerifier? wrapVerifier,
        CancellationToken cancellationToken)
    {
        if (await CheckWrapsAsync(wrapVerifier, plain, cancellationToken).ConfigureAwait(false) == KekWrapCheck.Failed)
        {
            await TryRecordAsync(audit, time, "kek.keyfile.migration_refused", keyringKind, "The plain KEK file does not unwrap any existing wrapped data key; nothing was sealed.").ConfigureAwait(false);
            throw new KekKeyringException("The plain KEK file does not unwrap any existing wrapped data key, so it is not this install's KEK. Restore the right key file; nothing was sealed or changed.");
        }

        // The old key stays on disk, owner-only, until the sealed key has been shown to unwrap an existing data key.
        var premigrate = keyPath + PremigrateSuffix;
        if (File.Exists(premigrate))
        {
            var kept = await File.ReadAllBytesAsync(premigrate, cancellationToken).ConfigureAwait(false);
            var same = CryptographicOperations.FixedTimeEquals(kept, plain);
            CryptographicOperations.ZeroMemory(kept);
            if (!same)
                throw new KekKeyringException($"A different {Path.GetFileName(premigrate)} already exists next to the KEK file; check which key is this install's and remove the other one.");
        }
        else
        {
            await DurableWriteAsync(premigrate, plain, overwrite: false, cancellationToken).ConfigureAwait(false);
        }

        await WriteSealedAsync(keyPath, plain, keyring, entry, overwrite: true, cancellationToken).ConfigureAwait(false);
        await WriteMarkerAsync(markerPath, keyringKind, entry).ConfigureAwait(false);
        await audit.RecordAsync(
            new KekCustodyAuditEvent(time.GetUtcNow(), "kek.keyfile.migrated", keyringKind, resumed ? "An interrupted migration was finished: the plain KEK file was sealed with the OS keyring." : "The plain KEK file was sealed with the OS keyring."),
            cancellationToken).ConfigureAwait(false);

        var sealedBytes = await File.ReadAllBytesAsync(keyPath, cancellationToken).ConfigureAwait(false);
        var reopened = await OpenSealedAsync(sealedBytes, keyring, cancellationToken).ConfigureAwait(false);
        try
        {
            await FinishPremigrateAsync(keyPath, reopened, audit, time, keyringKind, wrapVerifier, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(reopened);
            CryptographicOperations.ZeroMemory(sealedBytes);
        }
    }

    /// <summary>
    /// Removes <c>kek.bin.premigrate</c> once <paramref name="key"/> (read back from the sealed file) unwraps an existing data key or none exists. Without a
    /// verifier, or while the check fails, the copy stays and the open says so.
    /// </summary>
    private static async ValueTask FinishPremigrateAsync(string keyPath, byte[] key, IKekCustodyAuditSink audit, TimeProvider time, string keyringKind, IKekWrapVerifier? wrapVerifier, CancellationToken cancellationToken)
    {
        var premigrate = keyPath + PremigrateSuffix;
        if (!File.Exists(premigrate))
            return;

        var check = await CheckWrapsAsync(wrapVerifier, key, cancellationToken).ConfigureAwait(false);
        switch (check)
        {
            case null:
                await TryRecordAsync(audit, time, "kek.keyfile.premigrate_kept", keyringKind, "The pre-migration copy of the plain key is kept: no wrapped-key verifier is registered. Remove it by hand once the sealed key is confirmed.").ConfigureAwait(false);
                return;
            case KekWrapCheck.Failed:
                await TryRecordAsync(audit, time, "kek.keyfile.premigrate_kept", keyringKind, "The sealed KEK does not unwrap any existing wrapped data key; the pre-migration copy of the plain key is kept.").ConfigureAwait(false);
                throw new KekKeyringException($"The sealed KEK does not unwrap any existing wrapped data key. {Path.GetFileName(premigrate)} was kept; restore from it or from the recovery bundle (doc 48 section 6).");
            default:
                WipeFile(premigrate);
                await audit.RecordAsync(new KekCustodyAuditEvent(time.GetUtcNow(), "kek.keyfile.premigrate_removed", keyringKind, "The sealed KEK was verified; the pre-migration copy of the plain key was wiped."), cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private static async ValueTask<KekWrapCheck?> CheckWrapsAsync(IKekWrapVerifier? verifier, byte[] key, CancellationToken cancellationToken)
    {
        if (verifier is null)
            return null;

        using var candidate = FileKekProvider.FromKey((byte[])key.Clone());
        return await verifier.CheckAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    private static void WipeFile(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[length]);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the delete below still removes the name.
        }

        File.Delete(path);
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

    private static async ValueTask WriteSealedAsync(string keyPath, byte[] key, IKekKeyring keyring, string entry, bool overwrite, CancellationToken cancellationToken)
    {
        var kind = Encoding.UTF8.GetBytes(keyring.Kind);
        var entryBytes = Encoding.UTF8.GetBytes(entry);
        if (kind.Length is 0 or > 255)
            throw new KekKeyringException("The keyring kind must be 1 to 255 UTF-8 bytes.");
        if (entryBytes.Length is 0 or > 255)
            throw new KekKeyringException("The keyring entry name must be 1 to 255 UTF-8 bytes.");

        var payload = await keyring.SealAsync(entry, key, Aad(keyring.Kind, entry), cancellationToken).ConfigureAwait(false);
        var file = new byte[_fixedHeader + kind.Length + 1 + entryBytes.Length + payload.Length];
        _magic.CopyTo(file, 0);
        file[4] = _version;
        file[5] = (byte)kind.Length;
        kind.CopyTo(file, _fixedHeader);
        file[_fixedHeader + kind.Length] = (byte)entryBytes.Length;
        entryBytes.CopyTo(file, _fixedHeader + kind.Length + 1);
        payload.CopyTo(file, _fixedHeader + kind.Length + 1 + entryBytes.Length);

        // Verify before anything replaces the key: the bytes as written must open to the same key through the keyring.
        var check = await OpenSealedAsync(file, keyring, cancellationToken).ConfigureAwait(false);
        var same = CryptographicOperations.FixedTimeEquals(check, key);
        CryptographicOperations.ZeroMemory(check);
        if (!same)
            throw new KekKeyringException("The keyring did not return the KEK it sealed; the key file was not changed.");

        var temp = $"{keyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteTempAsync(temp, file, cancellationToken).ConfigureAwait(false);
            var back = await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false);
            var readBack = await OpenSealedAsync(back, keyring, cancellationToken).ConfigureAwait(false);
            same = CryptographicOperations.FixedTimeEquals(readBack, key);
            CryptographicOperations.ZeroMemory(readBack);
            if (!same)
                throw new KekKeyringException("The sealed KEK file did not read back; the key file was not changed.");

            File.Move(temp, keyPath, overwrite);
            DirectorySync.Flush(Path.GetDirectoryName(keyPath)!);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static async ValueTask WriteTempAsync(string temp, byte[] content, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(temp, options);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> owner-only and flushed, then fsyncs the directory.</summary>
    private static async ValueTask DurableWriteAsync(string path, byte[] content, bool overwrite, CancellationToken cancellationToken)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteTempAsync(temp, content, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite);
            DirectorySync.Flush(Path.GetDirectoryName(path)!);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static ValueTask WriteMarkerAsync(string markerPath, string kind, string entry) =>
        DurableWriteAsync(markerPath, Encoding.UTF8.GetBytes($"bazaar.kek.sealed.v1|{kind}|{entry}\n"), overwrite: true, CancellationToken.None);

    private readonly record struct SealedHeader(string Kind, string Entry, int PayloadOffset);

    private static SealedHeader ParseHeader(byte[] stored)
    {
        if (stored.Length < _fixedHeader + 1 || stored[4] != _version)
            throw new KekKeyringException("The sealed KEK file is malformed.");

        var kindLength = stored[5];
        var entryLengthAt = _fixedHeader + kindLength;
        if (kindLength == 0 || stored.Length < entryLengthAt + 1)
            throw new KekKeyringException("The sealed KEK file is malformed.");

        var entryLength = stored[entryLengthAt];
        var payloadAt = entryLengthAt + 1 + entryLength;
        if (entryLength == 0 || stored.Length < payloadAt)
            throw new KekKeyringException("The sealed KEK file is malformed.");

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return new SealedHeader(strict.GetString(stored, _fixedHeader, kindLength), strict.GetString(stored, entryLengthAt + 1, entryLength), payloadAt);
        }
        catch (DecoderFallbackException ex)
        {
            throw new KekKeyringException("The sealed KEK file is malformed.", ex);
        }
    }

    private static async ValueTask<byte[]> OpenSealedAsync(byte[] stored, IKekKeyring keyring, CancellationToken cancellationToken)
    {
        var header = ParseHeader(stored);
        if (!string.Equals(header.Kind, keyring.Kind, StringComparison.Ordinal))
            throw new KekKeyringException($"The KEK file is sealed by keyring '{header.Kind}', not by this host's '{keyring.Kind}'.");

        var key = await keyring.OpenAsync(header.Entry, stored.AsMemory(header.PayloadOffset), Aad(header.Kind, header.Entry), cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<FileStream> AcquireLockAsync(string lockPath, TimeSpan timeout, IKekCustodyAuditSink audit, TimeProvider time, string keyringKind, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, options);
            }
            catch (IOException ex)
            {
                if (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await TryRecordAsync(audit, time, "kek.keyfile.lock_timeout", keyringKind, "The KEK file lock could not be taken in time; the start is refused.").ConfigureAwait(false);
                throw new KekKeyringException($"The KEK file lock '{Path.GetFileName(lockPath)}' is held by another start and could not be taken within {timeout.TotalSeconds:0.#} s.", ex);
            }
        }
    }
}
