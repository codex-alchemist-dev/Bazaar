using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.SealedPassport;

/// <summary>
/// Sealed storage for Passport-backed browser profiles (doc 61 work package 4, doc 14 "Mounted folder" and "Browser"). Profile metadata lives in
/// scope <c>passport:&lt;agent-id&gt;</c>, each profile's cookie jar in <c>cookies:&lt;profile-id&gt;</c> and its Chromium profile folder in
/// <c>browserdir:&lt;profile-id&gt;</c>. Nothing is written in plaintext. A read whose sealed data fails verification throws
/// <see cref="PassportProfileUnavailableException"/>, returns no data and is audited.
/// </summary>
public interface ISealedPassportStore
{
    /// <summary>Creates a profile. Anonymous profiles take no allowlist; account profiles need at least one site.</summary>
    Task<BrowserProfileRef> CreateProfileAsync(string agentId, BrowserProfileClass kind, IEnumerable<string> serviceAllowlist, CancellationToken ct);

    /// <summary>Returns the profile, or null when it does not exist.</summary>
    Task<BrowserProfileRef?> FindProfileAsync(Guid profileId, CancellationToken ct);

    /// <summary>The agent's profiles, from its sealed manifest.</summary>
    Task<IReadOnlyList<BrowserProfileRef>> ListProfilesAsync(string agentId, CancellationToken ct);

    /// <summary>Decrypts the stored jar (empty, generation 0, when none was written).</summary>
    /// <exception cref="KeyNotFoundException">The profile does not exist.</exception>
    /// <exception cref="InvalidOperationException">The profile belongs to another agent.</exception>
    Task<SealedCookieJar> ReadCookiesAsync(string agentId, Guid profileId, CancellationToken ct);

    /// <summary>
    /// Seals the jar and returns its new generation. <paramref name="expectedGeneration"/> is required (no blind overwrite): 0 requires that no
    /// jar was written yet, a generation requires exactly that one (read it with <see cref="ReadCookiesAsync"/>). A stale one throws the store's
    /// generation conflict exception. An anonymous profile refuses any cookie (<see cref="InvalidOperationException"/>); a cookie outside the
    /// allowlist throws <see cref="ArgumentException"/>; another agent's profile throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<long> WriteCookiesAsync(string agentId, Guid profileId, IReadOnlyList<BrowserCookieEntry> cookies, long expectedGeneration, CancellationToken ct);

    /// <summary>
    /// Deletes the profile and crypto-shreds its cookie and folder scopes (doc 14, doc 48 section 9). Refused while its profile folder is open.
    /// Returns false when the agent has no such profile.
    /// </summary>
    Task<bool> DeleteProfileAsync(string agentId, Guid profileId, CancellationToken ct);

    /// <summary>Deletes every profile of the agent and crypto-shreds its <c>passport:&lt;agent-id&gt;</c> scope.</summary>
    Task ShredAgentAsync(string agentId, CancellationToken ct);
}

/// <summary>A decrypted cookie jar and the generation it was read at (0 when none was written).</summary>
public sealed record SealedCookieJar(long Generation, IReadOnlyList<BrowserCookieEntry> Cookies);

/// <summary>
/// Thrown when a profile's sealed data failed verification (edited, swapped, rolled back or truncated outside Bazaar). The caller gets no data;
/// recovery is restore from backup or an owner-approved reseal. The message never contains cookie values or key material.
/// </summary>
public sealed class PassportProfileUnavailableException(Guid? profileId, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The profile, when known.</summary>
    public Guid? ProfileId { get; } = profileId;
}

/// <summary>
/// Plaintext Chromium profile folders on the host (doc 61 WP4). A folder exists only while open, only strictly inside the operator's mount root
/// (tmpfs or the doc 63 encrypted volume, confirmed by the shared mount-root verifier), only when the operator set
/// <c>Bazaar:SealedPassport:AllowPlaintextProfileFolders</c>, and is zero-wiped when closed.
/// </summary>
public interface ISealedProfileDirectories
{
    /// <summary>Opens the profile's folder, restoring an account profile's sealed files. One open folder per profile at a time.</summary>
    /// <exception cref="InvalidOperationException">Plaintext folders are not allowed, no mount root, an unverified root, the profile is gone or
    /// another agent's, a link or foreign folder is in the way, or the folder is already open.</exception>
    /// <exception cref="PassportProfileUnavailableException">The sealed folder failed verification; nothing is left on disk.</exception>
    Task<ISealedProfileDirectory> OpenAsync(string agentId, Guid profileId, CancellationToken ct);
}

/// <summary>An open profile folder. Disposing reseals an account profile, then wipes the folder.</summary>
public interface ISealedProfileDirectory : IAsyncDisposable
{
    Guid ProfileId { get; }

    /// <summary>The full path of the plaintext folder, inside the mount root.</summary>
    string Path { get; }

    /// <summary>
    /// Seals the folder's current files (account profiles); a no-op for an anonymous profile, which is never sealed. Call it only after the browser
    /// has exited: a file Chromium is still writing can be sealed half written. Refused when the folder would push the scope over the store's bulk limit.
    /// </summary>
    Task ResealAsync(CancellationToken ct);
}

/// <summary>Result of importing one plaintext profile folder.</summary>
public sealed record PassportImportReport(Guid ProfileId, int Imported, int SkippedExisting, int RemovedPlaintext, bool SourceRemoved);

/// <summary>One-time, owner-started import of existing plaintext profile folders: seal, read back and compare, then zero-wipe (doc 61 WP7 path).</summary>
public interface ISealedPassportImporter
{
    /// <summary>
    /// Imports one folder into an existing account profile of <paramref name="agentId"/>. Plaintext is removed only after its sealed copy verified;
    /// the excluded cache folders are neither imported nor removed.
    /// </summary>
    Task<PassportImportReport> ImportProfileDirectoryAsync(string agentId, Guid profileId, string sourceDirectory, CancellationToken ct);
}
