namespace Bazaar.Server.AuditAnomaly;

/// <summary>Stream of events the rules read. A production source reads the audit chain; tests use a list.</summary>
public interface IAnomalyEventSource
{
    /// <summary>Events with <c>Ts</c> after <paramref name="after"/> and not after <paramref name="upTo"/>, any order.</summary>
    Task<IReadOnlyList<AnomalyEvent>> ReadAsync(DateTimeOffset after, DateTimeOffset upTo, CancellationToken ct);
}

/// <summary>Answers whether a read is inside an agent's scope (for the read-outside-scope rule).</summary>
public interface IAgentScopeProvider
{
    bool IsInScope(string principal, string objectType, string? objectId);
}

/// <summary>Delivers an alert to the owner's devices and security reviewers (notice channels, doc 15). No real notifier ships here.</summary>
public interface IAnomalyAlertSink
{
    Task NotifyAsync(AnomalyAlert alert, AlertAudience audience, CancellationToken ct);
}

/// <summary>
/// The kill-switch actions for one agent, one session or (owner only) the whole server. <see cref="RestoreAsync"/> returns the
/// prior state. The kill-switch adapter in the host implements this.
/// </summary>
public interface IContainmentPort
{
    Task ApplyAsync(ContainmentTarget target, ContainmentActions actions, CancellationToken ct);

    Task RestoreAsync(ContainmentTarget target, ContainmentActions actions, CancellationToken ct);
}

/// <summary>The owner's biometric check (doc 48 section 8). Anything but a clear true is a refusal.</summary>
public interface IBiometricCheck
{
    Task<bool> VerifyAsync(string principalId, string purpose, CancellationToken ct);
}

/// <summary>Holds containments so undo can find them.</summary>
public interface IContainmentStore
{
    Task SaveAsync(ContainmentRecord record, CancellationToken ct);

    Task<ContainmentRecord?> FindAsync(Guid id, CancellationToken ct);
}
