using Bazaar.Server.AgentSafety.Anomaly;

namespace Bazaar.Server.AgentSafety.AutoContainment;

/// <summary>What happened to an agent, for the owner's devices and the security reviewers.</summary>
public enum ContainmentNoticeKind
{
    /// <summary>The agent was contained; the owner may undo within the window.</summary>
    Contained,

    /// <summary>Containment was tried and failed.</summary>
    ContainmentFailed,

    /// <summary>The owner undid a containment.</summary>
    Undone,
}

/// <summary>A notice for humans. <see cref="Detail"/> is the rule's own short reason; it never carries secrets or message content.</summary>
public sealed record ContainmentNotice(
    ContainmentNoticeKind Kind,
    string AgentId,
    string RuleId,
    AnomalySeverity Severity,
    DateTimeOffset? UndoUntil,
    string Detail);

/// <summary>
/// Port to the notice channels of doc 15 (owner devices and security reviewers). The transport is not defined here; the host
/// supplies an implementation. A failing sink never blocks containment.
/// </summary>
public interface INoticeSink
{
    /// <summary>Delivers one notice.</summary>
    ValueTask PublishAsync(ContainmentNotice notice, CancellationToken ct);
}

/// <summary>The default sink: drops notices. Registered with TryAdd so a real sink replaces it.</summary>
public sealed class NullNoticeSink : INoticeSink
{
    /// <inheritdoc />
    public ValueTask PublishAsync(ContainmentNotice notice, CancellationToken ct) => ValueTask.CompletedTask;
}
