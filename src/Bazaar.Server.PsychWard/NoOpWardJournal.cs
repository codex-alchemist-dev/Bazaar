using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard;

/// <summary>The default <see cref="IWardJournal"/>: writes nothing. Used when the host has no GC log and principal directory to hand.</summary>
public sealed class NoOpWardJournal : IWardJournal
{
    /// <inheritdoc />
    public Task AdmittedAsync(WardStay stay, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CheckedInAsync(WardStay stay, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DischargedAsync(WardStay stay, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task VentWrittenAsync(VentDraft vent, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task VentPublishedAsync(VentDraft vent, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The directory used when the host supplies neither a principal directory nor an owner source. It knows nobody, so no GC entry is written.</summary>
internal sealed class NoWardPrincipalDirectory : IWardPrincipalDirectory
{
    public Task<Bazaar.Contracts.Gc.GcPrincipal?> GetOwnerAsync(CancellationToken cancellationToken) => Task.FromResult<Bazaar.Contracts.Gc.GcPrincipal?>(null);

    public Task<Bazaar.Contracts.Gc.GcPrincipal?> GetAgentAsync(Guid agentId, CancellationToken cancellationToken) => Task.FromResult<Bazaar.Contracts.Gc.GcPrincipal?>(null);
}

/// <summary>The owner source used without a database: no owner is known, so no GC entry is written.</summary>
internal sealed class NoWardOwnerSource : IWardOwnerSource
{
    public Task<Bazaar.Contracts.Gc.GcPrincipal?> GetOwnerAsync(CancellationToken cancellationToken) => Task.FromResult<Bazaar.Contracts.Gc.GcPrincipal?>(null);
}
