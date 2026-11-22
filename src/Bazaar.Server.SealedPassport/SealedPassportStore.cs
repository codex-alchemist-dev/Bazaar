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

    /// <inheritdoc />
    public async Task<BrowserProfileRef> CreateProfileAsync(string agentId, BrowserProfileClass kind, IEnumerable<string> serviceAllowlist, CancellationToken ct)
    {
        PassportScopes.EnsureSafeAgentId(agentId);
        ArgumentNullException.ThrowIfNull(serviceAllowlist);
        var allowlist = serviceAllowlist.Select(PassportDomains.Normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
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
    public async Task<SealedCookieJar> ReadCookiesAsync(Guid profileId, CancellationToken ct)
    {
        _ = await FindRecordAsync(profileId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Browser profile not found.");
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
    public async Task<long> WriteCookiesAsync(Guid profileId, IReadOnlyList<BrowserCookieEntry> cookies, long? expectedGeneration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var record = await FindRecordAsync(profileId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Browser profile not found.");
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
        return await GuardAsync(profileId, async () =>
        {
            using var agentScope = await store.OpenScopeAsync(PassportScopes.Agent(agentId), ct).ConfigureAwait(false);
            var path = PassportScopes.ProfilePath(profileId);
            using var index = await store.OpenScopeAsync(PassportScopes.Index, ct).ConfigureAwait(false);
            var indexed = await index.ReadAsync(PassportScopes.IndexPath(profileId), ct).ConfigureAwait(false);
            var metadata = (await agentScope.ListAsync(ct).ConfigureAwait(false)).Any(e => e.Path == path);
            if (indexed is not null && Encoding.UTF8.GetString(indexed.Content) != agentId)
            {
                throw new InvalidOperationException("Browser profile belongs to another agent.");
            }

            if (indexed is null && !metadata)
            {
                return false;
            }

            using var directory = await store.OpenScopeAsync(PassportScopes.Directory(profileId), ct).ConfigureAwait(false);
            try
            {
                // An open profile folder holds the lease; deleting under it would leave its plaintext without a sealed home.
                using var lease = await directory.AcquireLeaseAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The profile's folder is open; close the browser session before deleting the profile.");
            }

            // The index goes first, so the browser can no longer find the profile; the shreds make its data unrecoverable; the metadata goes last.
            if (indexed is not null)
            {
                await index.DeleteAsync(PassportScopes.IndexPath(profileId), indexed.Generation, ct).ConfigureAwait(false);
            }

            using (var cookies = await store.OpenScopeAsync(PassportScopes.Cookies(profileId), ct).ConfigureAwait(false))
            {
                await cookies.ShredAsync(ct).ConfigureAwait(false);
            }

            await directory.ShredAsync(ct).ConfigureAwait(false);
            await agentScope.DeleteAsync(path, ct).ConfigureAwait(false);
            await RecordAsync("passport.profile.shredded", agentScope.Scope, path, "Profile deleted; cookie and folder scopes crypto-shredded.", ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ShredAgentAsync(string agentId, CancellationToken ct)
    {
        PassportScopes.EnsureSafeAgentId(agentId);
        var ids = new HashSet<Guid>((await ListProfilesAsync(agentId, ct).ConfigureAwait(false)).Select(p => p.ProfileId));
        await GuardAsync<bool>(null, async () =>
        {
            // Index entries whose metadata is gone (a crash during an earlier delete) are found from the index side.
            using var index = await store.OpenScopeAsync(PassportScopes.Index, ct).ConfigureAwait(false);
            var snapshot = await index.ReadAllAsync(ct).ConfigureAwait(false);
            foreach (var file in snapshot.Files)
            {
                if (Encoding.UTF8.GetString(file.Content) == agentId && Guid.TryParse(file.Path["profiles/".Length..], out var id))
                {
                    ids.Add(id);
                }
            }

            return true;
        }, ct).ConfigureAwait(false);

        foreach (var id in ids)
        {
            await DeleteProfileAsync(agentId, id, ct).ConfigureAwait(false);
        }

        await GuardAsync(null, async () =>
        {
            using var scope = await store.OpenScopeAsync(PassportScopes.Agent(agentId), ct).ConfigureAwait(false);
            await scope.ShredAsync(ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        await RecordAsync("passport.agent.shredded", PassportScopes.Agent(agentId), null, $"Agent Passport scope crypto-shredded ({ids.Count} profiles).", ct).ConfigureAwait(false);
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
