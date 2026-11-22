using System.Security.Cryptography;
using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.SealedGit;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedPassport;

/// <summary>
/// Plaintext Chromium profile folders (doc 61 WP4). A folder exists only while open, strictly inside <see cref="SealedPassportOptions.MountRoot"/>,
/// which the shared <see cref="IMountRootVerifier"/> (the sealed Vault's concept, doc 61 "Vault adoption") must confirm as tmpfs or the doc 63
/// encrypted volume before any plaintext is written. An account profile's folder is restored from scope <c>browserdir:&lt;profile-id&gt;</c> and
/// resealed on close; an anonymous profile's folder is never sealed and starts empty. Every folder is zero-wiped on close. The scope lease is held
/// while the folder is open, so one profile has one open folder across processes.
/// </summary>
public sealed class SealedProfileDirectories(
    ISealedStore store,
    SealedPassportStore passport,
    SealedPassportOptions options,
    IMountRootVerifier? verifier) : ISealedProfileDirectories
{
    /// <summary>The marker next to a folder this module made; a folder with a marker found at open is a crashed session's leftover.</summary>
    public const string MarkerSuffix = ".bazaar-passport-mount";

    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _open = [];

    /// <inheritdoc />
    public async Task<ISealedProfileDirectory> OpenAsync(Guid profileId, CancellationToken ct)
    {
        var root = await VerifiedRootAsync(ct).ConfigureAwait(false);
        var profile = await passport.FindProfileAsync(profileId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Browser profile not found.");
        lock (_gate)
        {
            if (!_open.Add(profileId))
            {
                throw new InvalidOperationException("This profile's folder is already open.");
            }
        }

        ISealedScope? scope = null;
        ISealedLease? lease = null;
        var folder = Path.Combine(root, "profile-" + profileId.ToString("N"));
        var marker = folder + MarkerSuffix;
        var made = false;
        var markerWritten = false;
        try
        {
            scope = await store.OpenScopeAsync(PassportScopes.Directory(profileId), ct).ConfigureAwait(false);
            try
            {
                lease = await scope.AcquireLeaseAsync(options.LeaseTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException("This profile's folder is open in another process.", ex);
            }

            if (File.Exists(marker))
            {
                // A crashed session: its unsealed changes are discarded (protective default); the sealed copy is the truth.
                if (ProfileFolderWipe.WipeAndDelete(folder) is { } failure)
                {
                    await passport.RecordAsync("passport.profiledir.wipe_failed", scope.Scope, null, $"Stale folder could not be wiped ({failure.GetType().Name}).", ct).ConfigureAwait(false);
                    throw new InvalidOperationException("A stale plaintext profile folder could not be wiped; the open is refused.");
                }

                File.Delete(marker);
                await passport.RecordAsync("passport.profiledir.stale_wiped", scope.Scope, null, "Folder of a crashed session wiped.", ct).ConfigureAwait(false);
            }
            else if (Directory.Exists(folder) || File.Exists(folder))
            {
                await passport.RecordAsync("passport.profiledir.refused", scope.Scope, null, "A folder this module did not make is in the way; left untouched.", ct).ConfigureAwait(false);
                throw new InvalidOperationException("A profile folder that Bazaar did not make is in the way; it was left untouched and the open is refused.");
            }

            await File.WriteAllBytesAsync(marker, [], ct).ConfigureAwait(false);
            markerWritten = true;
            ProfileFolderWipe.CreateOwnerOnly(folder);
            made = true;

            var known = new Dictionary<string, (long Generation, byte[] Hash)>(StringComparer.Ordinal);
            if (profile.Class == BrowserProfileClass.Account)
            {
                await passport.GuardAsync(profileId, async () =>
                {
                    var snapshot = await scope.ReadAllAsync(ct).ConfigureAwait(false);
                    try
                    {
                        foreach (var file in snapshot.Files)
                        {
                            var target = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                            if (!SealedPaths.IsStrictlyInside(Path.GetFullPath(target), folder))
                            {
                                throw new SealTamperedException(scope.Scope, file.Path, "A sealed path points outside the profile folder.");
                            }

                            ProfileFolderWipe.CreateOwnerOnly(Path.GetDirectoryName(target)!);
                            await File.WriteAllBytesAsync(target, file.Content, ct).ConfigureAwait(false);
                            known[file.Path] = (file.Generation, SHA256.HashData(file.Content));
                        }
                    }
                    finally
                    {
                        foreach (var file in snapshot.Files)
                        {
                            CryptographicOperations.ZeroMemory(file.Content);
                        }
                    }

                    return true;
                }, ct).ConfigureAwait(false);
            }

            await passport.RecordAsync("passport.profiledir.open", scope.Scope, null, $"Profile folder opened ({profile.Class}).", ct).ConfigureAwait(false);
            return new OpenFolder(this, profile, scope, lease, folder, marker, known);
        }
        catch
        {
            if (made)
            {
                if (ProfileFolderWipe.WipeAndDelete(folder) is null)
                {
                    File.Delete(marker);
                }
            }
            else if (markerWritten)
            {
                File.Delete(marker);
            }

            lease?.Dispose();
            scope?.Dispose();
            Release(profileId);
            throw;
        }
    }

    private void Release(Guid profileId)
    {
        lock (_gate)
        {
            _open.Remove(profileId);
        }
    }

    /// <summary>The canonical mount root, after the floor checks: configured, no link component, owner-only (Unix), and confirmed by the verifier.</summary>
    private async Task<string> VerifiedRootAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.MountRoot))
        {
            throw new InvalidOperationException($"{SealedPassportOptions.SectionName}:MountRoot is not set; plaintext profile folders are refused.");
        }

        if (!Path.IsPathFullyQualified(options.MountRoot) || SealedPaths.HasLinkComponent(options.MountRoot))
        {
            await Refuse("The mount root must be a full path with no link in it.").ConfigureAwait(false);
        }

        var root = SealedPaths.Canonicalize(options.MountRoot);
        if (!Directory.Exists(root))
        {
            ProfileFolderWipe.CreateOwnerOnly(root);
        }

        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            await Refuse("The mount root is open to group or others.").ConfigureAwait(false);
        }

        if (verifier is null)
        {
            await Refuse("No mount-root verifier is registered, so the root cannot be confirmed as tmpfs or the encrypted volume.").ConfigureAwait(false);
        }

        var verdict = verifier!.Verify(root);
        if (!verdict.IsProtected)
        {
            await Refuse("The mount root is not confirmed as tmpfs or the encrypted volume: " + verdict.Reason).ConfigureAwait(false);
        }

        return root;

        async Task Refuse(string reason)
        {
            await passport.RecordAsync("passport.profiledir.mount_unverified", "browserdir:*", null, reason, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Plaintext profile folders are refused: " + reason);
        }
    }

    private sealed class OpenFolder(
        SealedProfileDirectories owner,
        BrowserProfileRef profile,
        ISealedScope scope,
        ISealedLease lease,
        string folder,
        string marker,
        Dictionary<string, (long Generation, byte[] Hash)> known) : ISealedProfileDirectory
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _closed;

        public Guid ProfileId => profile.ProfileId;

        public string Path => folder;

        public async Task ResealAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                await ResealCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ResealCoreAsync(CancellationToken ct)
        {
            if (profile.Class != BrowserProfileClass.Account)
            {
                return;
            }

            var (files, _, _) = ProfileFolderWipe.Walk(folder);
            var writes = new List<SealedWrite>();
            var buffers = new List<byte[]>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var file in files)
                {
                    var logical = System.IO.Path.GetRelativePath(folder, file.FullName).Replace('\\', '/');
                    if (logical.Split('/').Any(s => owner.Excluded(s)))
                    {
                        continue;
                    }

                    var bytes = await File.ReadAllBytesAsync(file.FullName, ct).ConfigureAwait(false);
                    buffers.Add(bytes);
                    seen.Add(logical);
                    if (known.TryGetValue(logical, out var prior))
                    {
                        if (!CryptographicOperations.FixedTimeEquals(prior.Hash, SHA256.HashData(bytes)))
                        {
                            writes.Add(SealedWrite.Put(logical, bytes, prior.Generation));
                        }
                    }
                    else
                    {
                        writes.Add(SealedWrite.Put(logical, bytes, 0));
                    }
                }

                foreach (var (path, prior) in known)
                {
                    if (!seen.Contains(path))
                    {
                        writes.Add(SealedWrite.Delete(path, prior.Generation));
                    }
                }

                if (writes.Count == 0)
                {
                    return;
                }

                await owner.Passport.GuardAsync(profile.ProfileId, async () => await scope.WriteBatchAsync(writes, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                var generations = (await scope.ListAsync(ct).ConfigureAwait(false)).ToDictionary(e => e.Path, e => e.Generation, StringComparer.Ordinal);
                foreach (var write in writes)
                {
                    if (write.IsDelete)
                    {
                        known.Remove(write.Path);
                    }
                    else
                    {
                        known[write.Path] = (generations[write.Path], SHA256.HashData(write.Content.Span));
                    }
                }
            }
            finally
            {
                foreach (var buffer in buffers)
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                try
                {
                    await ResealCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The plaintext floor wins: the folder is wiped anyway and the session's unsealed changes are lost; the sealed copy stays intact.
                    await owner.Passport.RecordAsync("passport.profiledir.reseal_failed", scope.Scope, null, $"Reseal failed ({ex.GetType().Name}); folder wiped, changes discarded.", CancellationToken.None).ConfigureAwait(false);
                }

                if (ProfileFolderWipe.WipeAndDelete(folder) is { } failure)
                {
                    // The marker stays, so the next open treats the folder as stale and wipes it again.
                    await owner.Passport.RecordAsync("passport.profiledir.wipe_failed", scope.Scope, null, $"Folder could not be wiped ({failure.GetType().Name}).", CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    File.Delete(marker);
                    await owner.Passport.RecordAsync("passport.profiledir.closed", scope.Scope, null, "Profile folder closed and wiped.", CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var entry in known.Values)
                {
                    CryptographicOperations.ZeroMemory(entry.Hash);
                }

                lease.Dispose();
                scope.Dispose();
                owner.Release(profile.ProfileId);
                _gate.Release();
            }
        }
    }

    private SealedPassportStore Passport => passport;

    private bool Excluded(string segment) => options.ExcludedFolders.Contains(segment, StringComparer.Ordinal);
}
