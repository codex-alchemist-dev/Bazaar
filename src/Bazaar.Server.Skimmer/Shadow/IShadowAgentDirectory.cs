namespace Bazaar.Server.Skimmer.Shadow;

/// <summary>What the gate needs to know about an agent that is not GC data: its mention handles and scoring profile.</summary>
public interface IShadowAgentDirectory
{
    /// <summary>Every agent the gate runs for, with the handles that count as a mention of it.</summary>
    Task<IReadOnlyList<ShadowAgentEntry>> ListAsync(CancellationToken ct);
}

/// <summary>One directory entry: the agent and its mention handles.</summary>
/// <param name="Agent">The agent and its scoring profile.</param>
/// <param name="Handles">The handles that count as a mention of the agent.</param>
public sealed record ShadowAgentEntry(ShadowAgentInfo Agent, IReadOnlySet<string> Handles);
