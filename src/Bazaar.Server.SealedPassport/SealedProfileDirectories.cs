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
/// while the folder is open, so one profile has one open folder across processes. Plaintext folders exist only when the operator set
/// <see cref="SealedPassportOptions.AllowPlaintextProfileFolders"/> (owner ruling 2026-11-22). Folders and markers that are links are refused and
/// never followed, and every wipe goes through <see cref="SecureFileWipe"/>.
/// </summary>
public sealed class SealedProfileDirectories : ISealedProfileDirectories
{
    /// <summary>The marker next to a folder this module made; a folder with a marker found at open is a crashed session's leftover.</summary>
    public const string MarkerSuffix = ".bazaar-passport-mount";

    private readonly ISealedStore _store;
    private readonly SealedPassportStore _passport;
    private readonly SealedPassportOptions _options;
    private readonly IMountRootVerifier? _verifier;
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _open = [];

    /// <summary>Test hook: runs after the profile is first looked up and before the folder lease is taken.</summary>
    internal Func<Task>? AfterProfileLookup { get; set; }

    public SealedProfileDirectories(ISealedStore store, SealedPassportStore passport, SealedPassportOptions options, IMountRootVerifier? verifier)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _passport = passport ?? throw new ArgumentNullException(nameof(passport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _verifier = verifier;

        // Lets the store's delete wipe a leftover folder while it holds the profile's folder lease.
        passport.Folders = this;
    }

    /// <inheritdoc />
    public async Task<ISealedProfileDirectory> OpenAsync(string agentId, Guid profileId, CancellationToken ct)
    {
        PassportScopes.EnsureSafeAgentId(agentId);
        var root = await VerifiedRootAsync(ct).ConfigureAwait(false);
        var profile = (await _passport.RequireOwnedAsync(agentId, profileId, ct).ConfigureAwait(false)).ToRef();
        if (AfterProfileLookup is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

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
            try
            {
                scope = await _store.OpenScopeAsync(PassportScopes.Directory(profileId), ct).ConfigureAwait(false);
                lease = await scope.AcquireLeaseAsync(_options.LeaseTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException("This profile's folder is open in another process.", ex);
            }
            catch (SealTamperedException ex)
            {
                // A delete shredded the scope under us (not tamper), or it really failed verification: tell them apart by looking for the profile.
                if (await _passport.FindRecordAsync(profileId, ct).ConfigureAwait(false) is null)
                {
                    await _passport.RecordAsync("passport.profiledir.refused", PassportScopes.Directory(profileId), null, "The profile was deleted while the folder was being opened; refused.", ct).ConfigureAwait(false);
                    throw new KeyNotFoundException("Browser profile not found.", ex);
                }

                await _passport.GuardAsync<bool>(profileId, () => Task.FromException<bool>(ex), ct).ConfigureAwait(false);
                throw;
            }

            // The profile may have been deleted between the lookup and the lease (a delete holds the lease until it is done): look again now that
            // the lease is ours, so a deleted profile never gets a folder.
            var current = await _passport.FindRecordAsync(profileId, ct).ConfigureAwait(false);
            if (current is null || !string.Equals(current.AgentId, agentId, StringComparison.Ordinal))
            {
                await _passport.RecordAsync("passport.profiledir.refused", scope.Scope, null, "The profile was deleted while the folder was being opened; refused.", ct).ConfigureAwait(false);
                throw new KeyNotFoundException("Browser profile not found.");
            }

            if (await WipeMarkedAsync(folder, marker, scope.Scope, ct).ConfigureAwait(false))
            {
                // A crashed session: its unsealed changes are discarded (protective default); the sealed copy is the truth.
                await _passport.RecordAsync("passport.profiledir.stale_wiped", scope.Scope, null, "Folder of a crashed session wiped.", ct).ConfigureAwait(false);
            }
            else if (SecureFileWipe.IsLink(folder) || Directory.Exists(folder) || File.Exists(folder))
            {
                await _passport.RecordAsync("passport.profiledir.refused", scope.Scope, null, "A folder this module did not make is in the way; left untouched.", ct).ConfigureAwait(false);
                throw new InvalidOperationException("A profile folder that Bazaar did not make is in the way; it was left untouched and the open is refused.");
            }

            await File.WriteAllBytesAsync(marker, [], ct).ConfigureAwait(false);
            markerWritten = true;
            ProfileFolderWipe.CreateOwnerOnly(folder);
            made = true;

            var known = new Dictionary<string, (long Generation, byte[] Hash)>(StringComparer.Ordinal);
            if (profile.Class == BrowserProfileClass.Account)
            {
                await _passport.GuardAsync(profileId, async () =>
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

            await _passport.RecordAsync("passport.profiledir.open", scope.Scope, null, $"Profile folder opened ({profile.Class}).", ct).ConfigureAwait(false);
            return new OpenFolder(this, profile, scope, lease, folder, marker, known, _store is SealedStore sealedStore ? sealedStore.MaxBulkBytes : null);
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

    /// <summary>
    /// If a marker is present for the folder, wipes the folder and removes the marker and returns true; false when there is no marker. Refuses (audited,
    /// <see cref="InvalidOperationException"/>) when the marker is a link or not a regular file, or the folder is a link: nothing is followed or wiped.
    /// </summary>
    private async Task<bool> WipeMarkedAsync(string folder, string marker, string scope, CancellationToken ct)
    {
        if (!SecureFileWipe.IsLink(marker) && !File.Exists(marker) && !Directory.Exists(marker))
        {
            return false;
        }

        if (SecureFileWipe.IsLink(marker) || !File.Exists(marker))
        {
            await _passport.RecordAsync("passport.profiledir.link_refused", scope, null, "The folder marker is a link or not a regular file; nothing was followed or wiped.", ct).ConfigureAwait(false);
            throw new InvalidOperationException("The profile folder marker is a link or not a regular file; the folder is left untouched and the open is refused.");
        }

        if (SecureFileWipe.IsLink(folder))
        {
            await _passport.RecordAsync("passport.profiledir.link_refused", scope, null, "The profile folder is a link; nothing was followed or wiped.", ct).ConfigureAwait(false);
            throw new InvalidOperationException("The profile folder is a link; it is left untouched and the open is refused.");
        }

        if (ProfileFolderWipe.WipeAndDelete(folder) is { } failure)
        {
            await _passport.RecordAsync("passport.profiledir.wipe_failed", scope, null, $"Stale folder could not be wiped ({failure.GetType().Name}).", ct).ConfigureAwait(false);
            throw new InvalidOperationException("A stale plaintext profile folder could not be wiped; the open is refused.");
        }

        File.Delete(marker);
        return true;
    }

    /// <summary>The canonical mount root for a wipe (no verifier needed: only marked folders are touched), or null when none is configured or it is unsafe.</summary>
    private async Task<string?> WipeRootAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.MountRoot))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(_options.MountRoot) || SealedPaths.HasLinkComponent(_options.MountRoot))
        {
            await _passport.RecordAsync("passport.profiledir.mount_unverified", "browserdir:*", null, "The mount root is not a full link-free path; no leftover folder was touched.", ct).ConfigureAwait(false);
            return null;
        }

        var root = SealedPaths.Canonicalize(_options.MountRoot);
        return Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// Wipes the profile's leftover folder if its marker is present. Called by a delete that holds the profile's folder lease. Never throws for a
    /// wipe problem (it is audited, and the sweep retries once the profile is gone), so an erasure is not blocked.
    /// </summary>
    internal async Task PurgeLeftoverAsync(Guid profileId, string scope, CancellationToken ct)
    {
        if (await WipeRootAsync(ct).ConfigureAwait(false) is not { } root)
        {
            return;
        }

        var folder = Path.Combine(root, "profile-" + profileId.ToString("N"));
        try
        {
            if (await WipeMarkedAsync(folder, folder + MarkerSuffix, scope, ct).ConfigureAwait(false))
            {
                await _passport.RecordAsync("passport.profiledir.purged", scope, null, "Leftover folder wiped by the profile's deletion.", ct).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Already audited by WipeMarkedAsync.
        }
    }

    /// <summary>
    /// Wipes every leftover folder under the mount root that has a marker (crashed sessions, deleted profiles), run once at component start. A
    /// folder whose profile still exists is wiped only when its folder lease is free, so another process's open folder is never touched. Markers
    /// whose name is not <c>profile-&lt;id&gt;</c> are audited and left. Returns the number of folders wiped.
    /// </summary>
    public async Task<int> SweepStaleAsync(CancellationToken ct)
    {
        if (await WipeRootAsync(ct).ConfigureAwait(false) is not { } root)
        {
            return 0;
        }

        var swept = 0;
        foreach (var marker in Directory.EnumerateFileSystemEntries(root, "*" + MarkerSuffix).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var stem = Path.GetFileName(marker)[..^MarkerSuffix.Length];
            if (!stem.StartsWith("profile-", StringComparison.Ordinal) || !Guid.TryParseExact(stem["profile-".Length..], "N", out var profileId))
            {
                await _passport.RecordAsync("passport.profiledir.unknown_marker", "browserdir:*", null, "A marker that is not profile-<id> was found in the mount root; left untouched.", ct).ConfigureAwait(false);
                continue;
            }

            lock (_gate)
            {
                if (_open.Contains(profileId))
                {
                    continue;
                }
            }

            ISealedScope? scope = null;
            ISealedLease? lease = null;
            try
            {
                ProfileRecord? record;
                try
                {
                    record = await _passport.FindRecordAsync(profileId, ct).ConfigureAwait(false);
                }
                catch (PassportProfileUnavailableException)
                {
                    // The profile's sealed data is tampered; the audit entry is written. Leave the folder: it may hold the only copy.
                    continue;
                }

                var scopeName = PassportScopes.Directory(profileId);
                if (record is not null)
                {
                    scope = await _store.OpenScopeAsync(scopeName, ct).ConfigureAwait(false);
                    try
                    {
                        lease = await scope.AcquireLeaseAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                }

                if (await WipeMarkedAsync(Path.Combine(root, stem), marker, scopeName, ct).ConfigureAwait(false))
                {
                    swept++;
                    await _passport.RecordAsync("passport.profiledir.stale_swept", scopeName, null, "Leftover folder wiped at start.", ct).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // Refused or failed; audited by WipeMarkedAsync.
            }
            finally
            {
                lease?.Dispose();
                scope?.Dispose();
            }
        }

        return swept;
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
        if (!_options.AllowPlaintextProfileFolders)
        {
            await Refuse($"{SealedPassportOptions.SectionName}:AllowPlaintextProfileFolders is false (the default); plaintext profile folders are refused.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(_options.MountRoot))
        {
            throw new InvalidOperationException($"{SealedPassportOptions.SectionName}:MountRoot is not set; plaintext profile folders are refused.");
        }

        if (!Path.IsPathFullyQualified(_options.MountRoot) || SealedPaths.HasLinkComponent(_options.MountRoot))
        {
            await Refuse("The mount root must be a full path with no link in it.").ConfigureAwait(false);
        }

        var root = SealedPaths.Canonicalize(_options.MountRoot);
        if (!Directory.Exists(root))
        {
            ProfileFolderWipe.CreateOwnerOnly(root);
        }

        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            await Refuse("The mount root is open to group or others.").ConfigureAwait(false);
        }

        if (OperatingSystem.IsWindows() && RootAcl.Check(root) is { } aclProblem)
        {
            await Refuse(aclProblem).ConfigureAwait(false);
        }

        if (_verifier is null)
        {
            await Refuse("No mount-root verifier is registered, so the root cannot be confirmed as tmpfs or the encrypted volume.").ConfigureAwait(false);
        }

        var verdict = _verifier!.Verify(root);
        if (!verdict.IsProtected)
        {
            await Refuse("The mount root is not confirmed as tmpfs or the encrypted volume: " + verdict.Reason).ConfigureAwait(false);
        }

        return root;

        async Task Refuse(string reason)
        {
            await _passport.RecordAsync("passport.profiledir.mount_unverified", "browserdir:*", null, reason, ct).ConfigureAwait(false);
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
        Dictionary<string, (long Generation, byte[] Hash)> known,
        long? maxBulkBytes) : ISealedProfileDirectory
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

            if (SecureFileWipe.IsLink(folder))
            {
                await owner._passport.RecordAsync("passport.profiledir.link_refused", scope.Scope, null, "The profile folder became a link; it was not resealed.", ct).ConfigureAwait(false);
                throw new InvalidOperationException("The profile folder is a link; it is not resealed.");
            }

            var (files, _, _) = SecureFileWipe.Walk(folder);
            var writes = new List<SealedWrite>();
            var buffers = new List<byte[]>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;
            try
            {
                // Name the files the way the store does (NFC, forward slashes, no colon): a name the store refuses is skipped and audited, never
                // allowed to fail the whole reseal; two names that normalise to one path keep the first.
                var candidates = new List<(FileInfo File, string Logical)>();
                foreach (var file in files.OrderBy(f => f.FullName, StringComparer.Ordinal))
                {
                    var relative = System.IO.Path.GetRelativePath(folder, file.FullName).Replace('\\', '/');
                    if (relative.Split('/').Any(s => owner.Excluded(s)))
                    {
                        continue;
                    }

                    string logical;
                    try
                    {
                        logical = SealedStore.NormaliseLogicalPath(relative);
                    }
                    catch (ArgumentException)
                    {
                        skipped++;
                        continue;
                    }

                    if (seen.Add(logical))
                    {
                        candidates.Add((file, logical));
                    }
                    else
                    {
                        skipped++;
                    }
                }

                if (skipped > 0)
                {
                    await owner._passport.RecordAsync("passport.profiledir.name_skipped", scope.Scope, null, $"{skipped} file name(s) the sealed store does not support were not sealed.", ct).ConfigureAwait(false);
                }

                // The scope must stay readable in one snapshot: refuse before sealing anything when the projected total would pass the bulk limit.
                if (maxBulkBytes is { } limit)
                {
                    const int perFileOverhead = 64;
                    var projected = candidates.Sum(c => c.File.Length + perFileOverhead);
                    if (projected > limit)
                    {
                        await owner._passport.RecordAsync("passport.profiledir.reseal_refused", scope.Scope, null, $"The folder holds {projected} bytes, over the {limit}-byte bulk limit; nothing was sealed.", ct).ConfigureAwait(false);
                        throw new InvalidOperationException($"The profile folder holds {projected} bytes, more than the {limit}-byte limit of one sealed scope; it is not resealed.");
                    }
                }

                seen.Clear();
                foreach (var (file, logical) in candidates)
                {
                    if (SecureFileWipe.IsLink(file))
                    {
                        // Swapped for a link after the walk: not sealed, its target is not read, and its sealed copy stays.
                        seen.Add(logical);
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

    private SealedPassportStore Passport => _passport;

    private bool Excluded(string segment) => _options.ExcludedFolders.Contains(segment, StringComparer.Ordinal);
}
