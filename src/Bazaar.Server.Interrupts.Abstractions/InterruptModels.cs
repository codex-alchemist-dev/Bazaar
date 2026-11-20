namespace Bazaar.Server.Interrupts;

/// <summary>Who may push an interrupt (doc 24 section 3). Agents are not a source: they mention each other instead.</summary>
public enum InterruptSource
{
    /// <summary>The owner, through an authenticated message.</summary>
    Owner,

    /// <summary>A build broke.</summary>
    SystemBuild,

    /// <summary>A security event.</summary>
    SystemSecurity,
}

/// <summary>Lifecycle of one frame. Only <see cref="Active"/> runs; <see cref="Done"/> and <see cref="Cancelled"/> are terminal.</summary>
public enum InterruptFrameState
{
    Active,
    Suspended,
    Done,
    Cancelled,
}

/// <summary>One row of <c>interrupt_frame</c>.</summary>
/// <param name="Id">The frame id.</param>
/// <param name="AgentId">The agent whose stack holds the frame.</param>
/// <param name="ParentFrameId">The frame this one interrupted, or null for the bottom of the stack.</param>
/// <param name="Source">Who pushed it.</param>
/// <param name="Reason">Why it was pushed. Short, owner- or system-written text.</param>
/// <param name="GcId">The group chat where the push is announced.</param>
/// <param name="State">The frame's state.</param>
/// <param name="PushedAt">When the frame was pushed, from the injected clock.</param>
/// <param name="TtlAt">When the frame expires, or null when it does not.</param>
/// <param name="Version">Incremented on every change to the row.</param>
public sealed record InterruptFrame(
    Guid Id,
    Guid AgentId,
    Guid? ParentFrameId,
    InterruptSource Source,
    string Reason,
    Guid GcId,
    InterruptFrameState State,
    DateTimeOffset PushedAt,
    DateTimeOffset? TtlAt,
    long Version);
