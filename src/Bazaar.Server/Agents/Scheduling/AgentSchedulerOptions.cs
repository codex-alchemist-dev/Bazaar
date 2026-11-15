namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// Settings for the agent wake loop, bound from the <c>Bazaar:AgentTurns:Scheduler</c> section. The loop is off by default:
/// no background service runs turns until an operator switches it on, and a model route must be named when it is on.
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

    /// <summary>The gateway route every woken turn uses. Required when <see cref="Enabled"/> is true.</summary>
    public string Route { get; init; } = "";

    /// <summary>The prefix of the turn holder name; the holder is <c>{HolderPrefix}:{agentId}</c>.</summary>
    public string HolderPrefix { get; init; } = "wake";

    /// <summary>The tools a woken turn may call. Empty means no tools.</summary>
    public List<string> ToolAllowlist { get; init; } = [];

    /// <summary>Checks the limits and the route. Throws on the first problem.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit, the poll interval or the guard backoff is out of range.</exception>
    /// <exception cref="ArgumentException">The holder prefix or an allowlist entry is blank, or the route is blank while enabled.</exception>
    public void Validate()
    {
        if (PollInterval < _minimumPollInterval)
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "Poll interval must be at least 100 ms.");
        ArgumentOutOfRangeException.ThrowIfLessThan(GuardTripBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAgentsPerPass, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentTurns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxItemsPerTurn, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(HolderPrefix, nameof(HolderPrefix));

        foreach (var tool in ToolAllowlist)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tool, nameof(ToolAllowlist));
        }

        if (Enabled && string.IsNullOrWhiteSpace(Route))
            throw new ArgumentException(
                $"{SectionName}:Route must name a gateway route when {SectionName}:Enabled is true.", nameof(Route));
    }
}
