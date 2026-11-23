namespace Bazaar.Server.Subagents;

/// <summary>A running subagent. <see cref="Completion"/> never faults; failures and cancellation arrive as a <see cref="SubagentResult"/>.</summary>
public sealed class SubagentHandle
{
    private readonly Action _cancel;

    /// <summary>Creates a handle.</summary>
    public SubagentHandle(string id, string chainId, int depth, Task<SubagentResult> completion, Action cancel)
    {
        Id = id;
        ChainId = chainId;
        Depth = depth;
        Completion = completion;
        _cancel = cancel;
    }

    /// <summary>The subagent id, as written to the trace.</summary>
    public string Id { get; }

    /// <summary>The parent chain this child counts against.</summary>
    public string ChainId { get; }

    /// <summary>The child's depth (parent depth + 1).</summary>
    public int Depth { get; }

    /// <summary>Completes when the run ended and its slot is released.</summary>
    public Task<SubagentResult> Completion { get; }

    /// <summary>Asks the run to stop.</summary>
    public void Cancel() => _cancel();
}
