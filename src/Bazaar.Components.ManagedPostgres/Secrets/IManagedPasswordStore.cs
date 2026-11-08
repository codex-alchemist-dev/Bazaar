namespace Bazaar.Components.ManagedPostgres.Secrets;

/// <summary>
/// Holds the managed cluster's superuser password between restarts. initdb runs once, then every later start needs the
/// password to connect. No production store is registered by this component yet; the Windows KEK-backed store plugs in
/// here. Managed mode refuses to start without one.
/// </summary>
public interface IManagedPasswordStore
{
    /// <summary>Returns the stored password, or null when none has been saved.</summary>
    Task<string?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Saves the password. Called before initdb runs, so a failed initdb can be retried with the same password.</summary>
    Task SaveAsync(string password, CancellationToken cancellationToken);
}
