namespace Bazaar.Server.Valkey;

/// <summary>
/// Supplies the client connection string for each use. The managed child changes its port and never reuses a stale address, so
/// callers ask for the string instead of keeping one. Implementations throw when no connection is available; callers treat that as a
/// degraded cache or a failed probe.
/// </summary>
public interface IValkeyConnectionSource
{
    /// <summary>Returns a connection string. It may contain a password; never log it.</summary>
    ValueTask<string> GetConnectionStringAsync(CancellationToken ct);
}
