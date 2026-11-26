namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// Settings for the agent wake loop, bound from the <c>Bazaar:AgentTurns:Scheduler</c> section. The loop is off by default:
/// no background service runs turns until an operator switches it on. Routes and tool grants are per agent (each agent's Soul
/// and model policy, doc 05), so this section holds only the loop's own limits and never names a route or a tool.
/// </summary>
public sealed class AgentSchedulerOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:AgentTurns:Scheduler";

    /// <summary>The smallest poll interval accepted. Shorter intervals only add database load.</summary>
    private static readonly TimeSpan _minimumPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Whether the wake loop runs. Off by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>How long the loop waits between passes.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Most agents one pass considers. Agents beyond this wait for a later pass.</summary>
    public int MaxAgentsPerPass { get; init; } = 20;

    /// <summary>Most turns running at once across all agents in one pass.</summary>
    public int MaxConcurrentTurns { get; init; } = 4;

    /// <summary>Most due inbox items one turn takes.</summary>
    public int MaxItemsPerTurn { get; init; } = 10;

    /// <summary>
    /// How long an agent the loop guard tripped is left alone before the guard is asked about it again. Zero asks on every pass.
    /// </summary>
    public TimeSpan GuardTripBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The prefix of the turn holder name; the holder is <c>{HolderPrefix}:{agentId}</c>.</summary>
    public string HolderPrefix { get; init; } = "wake";

    /// <summary>Checks the limits. Throws on the first problem.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit, the poll interval or the guard backoff is out of range.</exception>
    /// <exception cref="ArgumentException">The holder prefix is blank.</exception>
    public void Validate()
    {
        if (PollInterval < _minimumPollInterval)
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "Poll interval must be at least 100 ms.");
        ArgumentOutOfRangeException.ThrowIfLessThan(GuardTripBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAgentsPerPass, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentTurns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxItemsPerTurn, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(HolderPrefix, nameof(HolderPrefix));

    }
}
