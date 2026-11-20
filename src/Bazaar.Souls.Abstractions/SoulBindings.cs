namespace Bazaar.Souls.Abstractions;

/// <summary>
/// One version of one Soul: the pair that a message or judgment records as the identity it was produced under
/// (docs/design/07 "Versioning"). A reference, not a copy: nothing here holds Soul content.
/// </summary>
/// <param name="SoulId">The Soul.</param>
/// <param name="Version">The <c>soul_versions</c> version, 1 or more.</param>
public readonly record struct SoulRef(Guid SoulId, int Version)
{
    /// <summary>True when the version is a real version number.</summary>
    public bool IsValid => SoulId != Guid.Empty && Version >= 1;
}

/// <summary>
/// The read side of the agent-to-Soul mapping. Other modules (GC, judgments, the turn loop) depend on this interface only and
/// never read Souls tables, so there is no cross-module foreign key and every stamp is validated here at write time
/// (owner, 2026-11-16).
/// </summary>
public interface ISoulBindings
{
    /// <summary>
    /// The Soul version the agent runs under now: its bound Soul at the head of that Soul's version chain. Null when the agent
    /// has no binding, the Soul has no versions yet, or the agent is retired (a retired agent never wakes, doc 06).
    /// </summary>
    ValueTask<SoulRef?> ResolveAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// True when <paramref name="soul"/> names a real version and the agent is, or once was, bound to that Soul. A stamp from
    /// an earlier version, or from before a retirement, stays valid: history is never rewritten.
    /// </summary>
    ValueTask<bool> IsValidStampAsync(Guid agentId, SoulRef soul, CancellationToken cancellationToken);
}

/// <summary>The write side of the agent-to-Soul mapping. Append-only: a change is a new row, never an update.</summary>
public interface ISoulBindingAdmin
{
    /// <summary>
    /// Binds the agent to the Soul. The Soul must already have a version. Binding an agent to the Soul it already has is a
    /// no-op; binding to a different Soul records a rebind and later posts use the new Soul.
    /// </summary>
    /// <exception cref="InvalidOperationException">The Soul has no versions, or the agent is retired.</exception>
    Task BindAsync(Guid agentId, Guid soulId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Retires the agent's binding (docs/design/06 "Retired": Soul and history read-only). After this, <see cref="ISoulBindings.ResolveAsync"/>
    /// returns null for the agent. Retiring an unbound or already retired agent is a no-op.
    /// </summary>
    Task RetireAsync(Guid agentId, string reason, CancellationToken cancellationToken);
}
