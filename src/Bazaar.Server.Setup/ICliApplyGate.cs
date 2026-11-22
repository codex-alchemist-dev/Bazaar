using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Setup;

/// <summary>
/// Decides whether <c>setup apply</c> may run the applier. The gate fails closed: when <see cref="DurableAuditStoreAvailable"/> is
/// false, apply stops after the load, validation and hash checks, exits 2 and never calls the applier. The CLI composes a Postgres-backed
/// gate (<c>PostgresApplyGate</c>) that is open only when the audit store is reachable, with the real instance lock and audit log
/// registered through <see cref="RegisterApplyServices"/>.
/// </summary>
public interface ICliApplyGate
{
    /// <summary>True when a durable audit store (doc 15 audit_log) is registered, so an apply may write its audit rows.</summary>
    bool DurableAuditStoreAvailable { get; }

    /// <summary>
    /// Registers the instance lock, audit log and settings audit sink that the applier needs. Called only when
    /// <see cref="DurableAuditStoreAvailable"/> is true, so a closed gate never builds the applier.
    /// </summary>
    void RegisterApplyServices(IServiceCollection services);
}
