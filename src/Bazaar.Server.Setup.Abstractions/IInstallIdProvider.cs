namespace Bazaar.Server.Setup;

/// <summary>
/// The install's stable identity (doc 45 section 1.2, step 1 "Install id (generated)"). One id per install, created on first use and
/// never changed. It is not a secret and names no company, person or host; it only lets one install tell itself apart from another.
/// </summary>
public interface IInstallIdProvider
{
    /// <summary>
    /// Returns the install id, creating it on first call. Idempotent and safe under concurrent callers: every caller gets the same
    /// value for the life of the database.
    /// </summary>
    Task<Guid> GetOrCreateAsync(CancellationToken cancellationToken = default);
}
