using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedPassport;

/// <summary>
/// The sealed Passport profile store (doc 61 WP4). Every byte goes through <see cref="ISealedStore"/>; tamper fails closed with a
/// <see cref="PassportProfileUnavailableException"/> and a <c>passport.tamper</c> audit entry next to the store's own <c>seal.tamper</c>.
/// Decrypted buffers are zeroed after use; cookie values in the returned records are managed strings and cannot be zeroed.
/// </summary>
public sealed class SealedPassportStore(ISealedStore store, ISealAuditSink audit, TimeProvider? time = null) : ISealedPassportStore, IBrowserProfileDirectory
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Set by <see cref="SealedProfileDirectories"/>: wipes a leftover plaintext folder (found by its marker) while a delete holds the profile's folder lease.</summary>
    internal SealedProfileDirectories? Folders { get; set; }

    /// <inheritdoc />
    public async Task<BrowserProfileRef> CreateProfileAsync(string agentId, BrowserProfileClass kind, IEnumerable<string> serviceAllowlist, CancellationToken ct)
    {
        PassportScopes.EnsureSafeAgentId(agentId);
        ArgumentNullException.ThrowIfNull(serviceAllowlist);
        var allowlist = serviceAllowlist.Select(PassportDomains.NormalizeAllowlistEntry).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (kind == BrowserProfileClass.Anonymous && allowlist.Length > 0)
        {
            throw new ArgumentException("An anonymous profile takes no service allowlist.", nameof(serviceAllowlist));
        }

        if (kind == BrowserProfileClass.Account && allowlist.Length == 0)
        {
            throw new ArgumentException("An account profile needs at least one allowlisted site.", nameof(serviceAllowlist));
        }

        var record = new ProfileRecord(Guid.NewGuid(), agentId, kind, allowlist);
        await GuardAsync(record.ProfileId, async () =>
        {
            // Metadata first, then the index: a crash in between leaves a profile the agent lists and can delete, never an index entry without data.
            using (var agentScope = await store.OpenScopeAsync(PassportScopes.Agent(agentId), ct).ConfigureAwait(false))
            {
                await agentScope.WriteAsync(PassportScopes.ProfilePath(record.ProfileId), JsonSerializer.SerializeToUtf8Bytes(record, PassportJsonContext.Default.ProfileRecord), 0, ct).ConfigureAwait(false);
            }

            using var index = await store.OpenScopeAsync(PassportScopes.Index, ct).ConfigureAwait(false);
            await index.WriteAsync(PassportScopes.IndexPath(record.ProfileId), Encoding.UTF8.GetBytes(agentId), 0, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        await RecordAsync("passport.profile.created", PassportScopes.Agent(agentId), PassportScopes.ProfilePath(record.ProfileId), $"Profile created ({kind}).", ct).ConfigureAwait(false);
        return record.ToRef();
    }

    /// <inheritdoc />
    public async Task<BrowserProfileRef?> FindProfileAsync(Guid profileId, CancellationToken ct) =>
        (await FindRecordAsync(profileId, ct).ConfigureAwait(false))?.ToRef();

    /// <inheritdoc />
    Task<BrowserProfileRef?> IBrowserProfileDirectory.FindAsync(Guid profileId, CancellationToken ct) => FindProfileAsync(profileId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<BrowserProfileRef>> ListProfilesAsync(string agentId, CancellationToken ct)
    {
        PassportScopes.EnsureSafeAgentId(agentId);
        return GuardAsync<IReadOnlyList<BrowserProfileRef>>(null, async () =>
        {
            using var scope = await store.OpenScopeAsync(PassportScopes.Agent(agentId), ct).ConfigureAwait(false);
            var snapshot = await scope.ReadAllAsync(ct).ConfigureAwait(false);
            var result = new List<BrowserProfileRef>();
            foreach (var file in snapshot.Files)
            {
                try
                {
                    var record = ParseRecord(scope.Scope, file);
                    result.Add(record.ToRef());
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(file.Content);
                }
            }

            return result;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<SealedCookieJar> ReadCookiesAsync(string agentId, Guid profileId, CancellationToken ct)
    {
        _ = await RequireOwnedAsync(agentId, profileId, ct).ConfigureAwait(false);
        return await GuardAsync(profileId, async () =>
        {
            using var scope = await store.OpenScopeAsync(PassportScopes.Cookies(profileId), ct).ConfigureAwait(false);
            var file = await scope.ReadAsync(PassportScopes.JarPath, ct).ConfigureAwait(false);
            if (file is null)
            {
                return new SealedCookieJar(0, []);
            }

            try
            {
                var cookies = JsonSerializer.Deserialize(file.Content, PassportJsonContext.Default.BrowserCookieEntryArray)
                    ?? throw new SealTamperedException(scope.Scope, file.Path, "Cookie jar payload is empty.");
                return new SealedCookieJar(file.Generation, cookies);
            }
            catch (JsonException)
            {
                throw new SealTamperedException(scope.Scope, file.Path, "Cookie jar payload is not a valid jar.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(file.Content);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> WriteCookiesAsync(string agentId, Guid profileId, IReadOnlyList<BrowserCookieEntry> cookies, long expectedGeneration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var record = await RequireOwnedAsync(agentId, profileId, ct).ConfigureAwait(false);
        if (record.Class == BrowserProfileClass.Anonymous && cookies.Count > 0)
        {
            throw new InvalidOperationException("Anonymous browser profiles never hold cookies.");
        }

        foreach (var cookie in cookies)
        {
            ArgumentNullException.ThrowIfNull(cookie);
            if (!record.ServiceAllowlist.Any(a => PassportDomains.IsWithin(cookie.Domain, a)))
            {
                // ToString prints name, domain and path only, never the value.
                throw new ArgumentException($"Cookie {cookie} is outside this profile's service allowlist.", nameof(cookies));
            }
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(cookies.ToArray(), PassportJsonContext.Default.BrowserCookieEntryArray);
        try
        {
            return await GuardAsync(profileId, async () =>
            {
                using var scope = await store.OpenScopeAsync(PassportScopes.Cookies(profileId), ct).ConfigureAwait(false);
                return await scope.WriteAsync(PassportScopes.JarPath, payload, expectedGeneration, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProfileAsync(string agentId, Guid profileId, CancellationToken ct)
    {
        PassportScopes.EnsureSafeAgentId(agentId);
        return await GuardAsync(profileId, () => EraseAsync(agentId, profileId, tolerant: false, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes one profile of the agent. With <paramref name="tolerant"/> (erasure) a tampered index entry or listing is audited and the shred goes on:
    /// erasure must never be blocked by the corruption it is meant to remove. The folder scope's lease is held from the check through the shreds
    /// (on Windows it is released just before the folder scope itself is shredded, because an open lease file cannot be deleted there; by then the index
    /// entry is gone and an open re-checks the profile after it takes the lease, so no open can complete).
    /// </summary>
    private async Task<bool> EraseAsync(string agentId, Guid profileId, bool tolerant, CancellationToken ct)
    {
        using var agentScope = await OpenForEraseAsync(PassportScopes.Agent(agentId), tolerant, ct).ConfigureAwait(false);
        var path = PassportScopes.ProfilePath(profileId);
        using var index = await OpenForEraseAsync(PassportScopes.Index, tolerant, ct).ConfigureAwait(false);
        SealedFile? indexed = null;
        var indexTampered = index is null;
        if (index is not null)
        {
            try
            {
                indexed = await index.ReadAsync(PassportScopes.IndexPath(profileId), ct).ConfigureAwait(false);
            }
            catch (SealTamperedException ex) when (tolerant)
            {
                indexTampered = true;
                await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Passport index entry failed verification during erasure; erasure carries on.", ct).ConfigureAwait(false);
            }
        }

        // An agent scope that cannot be opened is treated as holding the profile: the shred below does not need it.
        var metadata = agentScope is null;
        if (agentScope is not null)
        {
            try
            {
                metadata = (await agentScope.ListAsync(ct).ConfigureAwait(false)).Any(e => e.Path == path);
            }
            catch (SealTamperedException ex) when (tolerant)
            {
                metadata = true;
                await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Agent scope listing failed verification during erasure; erasure carries on.", ct).ConfigureAwait(false);
            }
        }

        try
        {
            if (indexed is not null && Encoding.UTF8.GetString(indexed.Content) != agentId)
            {
                throw new InvalidOperationException("Browser profile belongs to another agent.");
            }
        }
        finally
        {
            if (indexed is not null)
            {
                CryptographicOperations.ZeroMemory(indexed.Content);
            }
        }

        if (indexed is null && !indexTampered && !metadata)
        {
            return false;
        }

        var directoryName = PassportScopes.Directory(profileId);
        using var directory = await OpenForEraseAsync(directoryName, tolerant, ct).ConfigureAwait(false);
        ISealedLease? lease = null;
        if (directory is not null)
        {
            try
            {
                // An open profile folder holds the lease; deleting under it would leave its plaintext without a sealed home.
                lease = await directory.AcquireLeaseAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The profile's folder is open; close the browser session before deleting the profile.");
            }
        }

        using (lease)
        {
            if (Folders is { } folders)
            {
                // A crashed session's plaintext folder (found by its marker) goes before its sealed home does.
                await folders.PurgeLeftoverAsync(profileId, directoryName, ct).ConfigureAwait(false);
            }

            // The index goes first, so the browser can no longer find the profile; the shreds make its data unrecoverable; the metadata goes last.
            try
            {
                if (index is not null && (indexed is not null || indexTampered))
                {
                    await index.DeleteAsync(PassportScopes.IndexPath(profileId), indexed?.Generation, ct).ConfigureAwait(false);
                }
            }
            catch (SealTamperedException ex) when (tolerant)
            {
                await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Passport index entry could not be removed during erasure; its data is shredded anyway.", ct).ConfigureAwait(false);
            }

            await ShredByNameAsync(PassportScopes.Cookies(profileId), tolerant, ct).ConfigureAwait(false);

            if (OperatingSystem.IsWindows())
            {
                lease?.Dispose();
            }

            if (directory is not null)
            {
                await ShredTolerantAsync(directory, tolerant, ct).ConfigureAwait(false);
            }
            else
            {
                await ForceShredAsync(directoryName, ct).ConfigureAwait(false);
            }
        }

        if (agentScope is not null)
        {
            try
            {
                await agentScope.DeleteAsync(path, ct).ConfigureAwait(false);
            }
            catch (SealTamperedException ex) when (tolerant)
            {
                await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Profile metadata could not be removed during erasure; the agent scope is shredded next.", ct).ConfigureAwait(false);
            }
        }

        await RecordAsync("passport.profile.shredded", PassportScopes.Agent(agentId), path, "Profile deleted; cookie and folder scopes crypto-shredded.", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Opens a scope for a delete. During erasure a scope that fails verification is audited and returns null (it is force-shredded later) instead of stopping the erasure.</summary>
    private async Task<ISealedScope?> OpenForEraseAsync(string scopeName, bool tolerant, CancellationToken ct)
    {
        try
        {
            return await store.OpenScopeAsync(scopeName, ct).ConfigureAwait(false);
        }
        catch (SealTamperedException ex) when (tolerant)
        {
            await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Scope failed verification during erasure; it is shredded without being read.", ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task ShredByNameAsync(string scopeName, bool tolerant, CancellationToken ct)
    {
        using var scope = await OpenForEraseAsync(scopeName, tolerant, ct).ConfigureAwait(false);
        if (scope is null)
        {
            await ForceShredAsync(scopeName, ct).ConfigureAwait(false);
        }
        else
        {
            await ShredTolerantAsync(scope, tolerant, ct).ConfigureAwait(false);
        }
    }

    private async Task ShredTolerantAsync(ISealedScope scope, bool tolerant, CancellationToken ct)
    {
        try
        {
            await scope.ShredAsync(ct).ConfigureAwait(false);
        }
        catch (SealTamperedException ex) when (tolerant)
        {
            await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Scope failed verification during erasure; shredding carries on.", ct).ConfigureAwait(false);
            await ForceShredAsync(scope.Scope, ct).ConfigureAwait(false);
        }
    }

    private async Task ForceShredAsync(string scopeName, CancellationToken ct)
    {
        if (store is ISealedScopeShredder shredder)
        {
            await shredder.ForceShredScopeAsync(scopeName, ct).ConfigureAwait(false);
        }
        else
        {
            await RecordAsync("passport.shred_unavailable", scopeName, null, "The store cannot shred a scope that failed verification; its ciphertext remains sealed.", ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ShredAgentAsync(string agentId, CancellationToken ct)
    {
        PassportScopes.EnsureSafeAgentId(agentId);

        // Ids come from file names only (never from parsed metadata), so tampered metadata cannot stop an erasure.
        var ids = new HashSet<Guid>();
        try
        {
            using var agentScope = await store.OpenScopeAsync(PassportScopes.Agent(agentId), ct).ConfigureAwait(false);
            foreach (var entry in await agentScope.ListAsync(ct).ConfigureAwait(false))
            {
                if (entry.Path.StartsWith("profiles/", StringComparison.Ordinal) && entry.Path.EndsWith(".json", StringComparison.Ordinal)
                    && Guid.TryParse(entry.Path["profiles/".Length..^".json".Length], out var id))
                {
                    ids.Add(id);
                }
            }
        }
        catch (SealTamperedException ex)
        {
            await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Agent scope failed verification during erasure; erasure carries on.", ct).ConfigureAwait(false);
        }

        try
        {
            // Index entries whose metadata is gone (a crash during an earlier delete) are found from the index side; an entry that cannot be read
            // cannot be attributed to the agent and is left alone (audited).
            using var index = await store.OpenScopeAsync(PassportScopes.Index, ct).ConfigureAwait(false);
            foreach (var entry in await index.ListAsync(ct).ConfigureAwait(false))
            {
                if (!entry.Path.StartsWith("profiles/", StringComparison.Ordinal) || !Guid.TryParse(entry.Path["profiles/".Length..], out var id))
                {
                    continue;
                }

                try
                {
                    var file = await index.ReadAsync(entry.Path, ct).ConfigureAwait(false);
                    if (file is not null)
                    {
                        try
                        {
                            if (Encoding.UTF8.GetString(file.Content) == agentId)
                            {
                                ids.Add(id);
                            }
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(file.Content);
                        }
                    }
                }
                catch (SealTamperedException ex)
                {
                    await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Passport index entry failed verification during erasure; it cannot be attributed and is left alone.", ct).ConfigureAwait(false);
                }
            }
        }
        catch (SealTamperedException ex)
        {
            await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Passport index failed verification during erasure; erasure carries on.", ct).ConfigureAwait(false);
        }

        foreach (var id in ids)
        {
            await EraseAsync(agentId, id, tolerant: true, ct).ConfigureAwait(false);
        }

        await ShredByNameAsync(PassportScopes.Agent(agentId), tolerant: true, ct).ConfigureAwait(false);

        await RecordAsync("passport.agent.shredded", PassportScopes.Agent(agentId), null, $"Agent Passport scope crypto-shredded ({ids.Count} profiles).", ct).ConfigureAwait(false);
    }

    /// <summary>The profile's sealed record, refusing a missing profile and another agent's.</summary>
    internal async Task<ProfileRecord> RequireOwnedAsync(string agentId, Guid profileId, CancellationToken ct)
    {
        PassportScopes.EnsureSafeAgentId(agentId);
        var record = await FindRecordAsync(profileId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Browser profile not found.");
        if (!string.Equals(record.AgentId, agentId, StringComparison.Ordinal))
        {
            await RecordAsync("passport.profile.refused", PassportScopes.Agent(agentId), null, "A profile of another agent was asked for; refused.", ct).ConfigureAwait(false);
            throw new InvalidOperationException("Browser profile belongs to another agent.");
        }

        return record;
    }

    internal async Task<ProfileRecord?> FindRecordAsync(Guid profileId, CancellationToken ct) =>
        await GuardAsync(profileId, async () =>
        {
            string agentId;
            using (var index = await store.OpenScopeAsync(PassportScopes.Index, ct).ConfigureAwait(false))
            {
                var entry = await index.ReadAsync(PassportScopes.IndexPath(profileId), ct).ConfigureAwait(false);
                if (entry is null)
                {
                    return null;
                }

                agentId = Encoding.UTF8.GetString(entry.Content);
            }

            PassportScopes.EnsureSafeAgentId(agentId);
            using var scope = await store.OpenScopeAsync(PassportScopes.Agent(agentId), ct).ConfigureAwait(false);
            var file = await scope.ReadAsync(PassportScopes.ProfilePath(profileId), ct).ConfigureAwait(false)
                ?? throw new SealTamperedException(scope.Scope, PassportScopes.ProfilePath(profileId), "Profile is indexed but its metadata is missing.");
            try
            {
                var record = ParseRecord(scope.Scope, file);
                return record.ProfileId == profileId && record.AgentId == agentId
                    ? record
                    : throw new SealTamperedException(scope.Scope, file.Path, "Profile metadata does not match its index entry.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(file.Content);
            }
        }, ct).ConfigureAwait(false);

    private static ProfileRecord ParseRecord(string scope, SealedFile file)
    {
        try
        {
            return JsonSerializer.Deserialize(file.Content, PassportJsonContext.Default.ProfileRecord)
                ?? throw new SealTamperedException(scope, file.Path, "Profile metadata is empty.");
        }
        catch (JsonException)
        {
            throw new SealTamperedException(scope, file.Path, "Profile metadata is not valid.");
        }
    }

    /// <summary>Turns sealed-storage tamper into the Passport's clear error plus an audit entry. Generation conflicts and other errors pass through.</summary>
    internal async Task<T> GuardAsync<T>(Guid? profileId, Func<Task<T>> operation, CancellationToken ct)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SealTamperedException ex)
        {
            await RecordAsync("passport.tamper", ex.Scope, ex.Path, "Sealed Passport data failed verification; access refused (fail closed).", ct).ConfigureAwait(false);
            throw new PassportProfileUnavailableException(
                profileId,
                "This browser profile's sealed data failed verification and cannot be used. Restore it from backup or have the owner reseal it.",
                ex);
        }
    }

    internal async ValueTask RecordAsync(string kind, string scope, string? path, string detail, CancellationToken ct)
    {
        try
        {
            await audit.RecordAsync(new SealAuditEvent(_time.GetUtcNow(), kind, scope, path, detail), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure to record never hides the outcome: the caller still gets the exception or result.
        }
    }
}
