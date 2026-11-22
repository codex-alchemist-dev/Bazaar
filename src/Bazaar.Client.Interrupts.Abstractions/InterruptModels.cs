using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Client.Interrupts;

/// <summary>Who pushed an interrupt frame (doc 24 section 3; wire names owner, system_build, system_security).</summary>
public enum InterruptSource
{
    Owner,
    SystemBuild,
    SystemSecurity,
}

/// <summary>Lifecycle state of an interrupt frame (wire names active, suspended, done, cancelled).</summary>
public enum InterruptFrameState
{
    Active,
    Suspended,
    Done,
    Cancelled,
}

/// <summary>One frame of an agent's interrupt stack, as the client shows it. <see cref="Reason"/> may be personal text: never log it.</summary>
public sealed record InterruptFrameSummary(
    string Id,
    InterruptSource Source,
    string Reason,
    InterruptFrameState State,
    DateTimeOffset PushedAt)
{
    /// <summary>True while the frame counts toward the stack depth (active or suspended).</summary>
    public bool IsLive => State is InterruptFrameState.Active or InterruptFrameState.Suspended;
}

/// <summary>One agent's interrupt stack. The decided maximum depth is <see cref="MaxDepth"/> (doc 24, C35).</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The name is fixed by the design (doc 24); it is a record of one agent's interrupt stack, not a collection.")]
public sealed record AgentInterruptStack(string AgentId, string Name, IReadOnlyList<InterruptFrameSummary> Frames)
{
    /// <summary>The decided stack limit. A model with more live frames is bad data.</summary>
    public const int MaxDepth = 2;

    /// <summary>Live frames, top first: the active frame, then suspended frames newest first.</summary>
    public IReadOnlyList<InterruptFrameSummary> LiveFramesTopFirst =>
        Frames.Where(f => f.IsLive)
            .OrderBy(f => f.State == InterruptFrameState.Active ? 0 : 1)
            .ThenByDescending(f => f.PushedAt)
            .ToArray();

    /// <summary>The number of live frames as reported, which can exceed <see cref="MaxDepth"/> on bad data.</summary>
    public int LiveFrameCount => Frames.Count(f => f.IsLive);

    /// <summary>The depth to show: the live frame count capped at <see cref="MaxDepth"/>.</summary>
    public int Depth => Math.Min(LiveFrameCount, MaxDepth);

    /// <summary>True when the model carries more live frames than the stack allows.</summary>
    public bool IsOverLimit => LiveFrameCount > MaxDepth;

    /// <summary>The top live frame, or null at depth 0.</summary>
    public InterruptFrameSummary? Top => LiveFramesTopFirst is { Count: > 0 } live ? live[0] : null;
}

/// <summary>Optional source of interrupt stacks for a head that embeds the indicator outside a pane.</summary>
public interface IInterruptStackSource
{
    /// <summary>The current stacks of every agent the viewer may see.</summary>
    ValueTask<IReadOnlyList<AgentInterruptStack>> GetStacksAsync(CancellationToken cancellationToken);
}
