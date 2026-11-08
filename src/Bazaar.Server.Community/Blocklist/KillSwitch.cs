namespace Bazaar.Server.Community.Blocklist;

/// <summary>Result of <see cref="KillSwitch.CheckAsync"/>.</summary>
/// <param name="Blocked">True when the action must not proceed.</param>
/// <param name="Scope">Which switch blocked it: <see cref="KillSwitch.PlatformScope"/>, <see cref="KillSwitch.AgentScope"/> or <see cref="KillSwitch.TargetScope"/>. Null when not blocked.</param>
/// <param name="Reason">A short, human-readable reason. Null when not blocked.</param>
public sealed record KillSwitchDecision(bool Blocked, string? Scope, string? Reason)
{
    /// <summary>A decision that lets the action proceed.</summary>
    public static KillSwitchDecision Allowed { get; } = new(false, null, null);
}

/// <summary>
/// Stops community writes at three levels (doc 36, "Rate limits, audit and takedown"): a whole platform, one agent, or one
/// target. Platform and agent stops are held in memory and cleared on restart. Target entries go to
/// <see cref="ITargetBlocklist"/>, which keeps them. Resuming a platform or agent is an owner action; this class does not
/// check who calls it, so the caller must authorize it.
/// </summary>
public sealed class KillSwitch(ITargetBlocklist blocklist)
{
    /// <summary>Scope name for a stopped platform.</summary>
    public const string PlatformScope = "platform";

    /// <summary>Scope name for a stopped agent.</summary>
    public const string AgentScope = "agent";

    /// <summary>Scope name for a blocklisted target.</summary>
    public const string TargetScope = "target";

    private readonly ITargetBlocklist _blocklist = blocklist ?? throw new ArgumentNullException(nameof(blocklist));
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _stoppedPlatforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stoppedAgents = new(StringComparer.Ordinal);

    /// <summary>
    /// Checks the platform stop, then the agent stop, then the target blocklist. The first switch that is on decides the result.
    /// </summary>
    /// <param name="agentId">The acting agent.</param>
    /// <param name="platform">The platform the action targets.</param>
    /// <param name="targetRef">The target, or null when the action has none. The blocklist is not checked without one.</param>
    public async Task<KillSwitchDecision> CheckAsync(string agentId, string platform, string? targetRef = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId, nameof(agentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(platform, nameof(platform));

        string? platformReason;
        string? agentReason;
        lock (_gate)
        {
            _stoppedPlatforms.TryGetValue(platform, out platformReason);
            _stoppedAgents.TryGetValue(agentId, out agentReason);
        }

        if (platformReason is not null)
            return new KillSwitchDecision(true, PlatformScope, platformReason);
        if (agentReason is not null)
            return new KillSwitchDecision(true, AgentScope, agentReason);
        if (targetRef is not null && await _blocklist.IsBlockedAsync(platform, targetRef, ct).ConfigureAwait(false))
            return new KillSwitchDecision(true, TargetScope, "Target is on the blocklist.");

        return KillSwitchDecision.Allowed;
    }

    /// <summary>Stops every community write on the platform until <see cref="ResumePlatform"/> is called.</summary>
    /// <param name="platform">The platform to stop.</param>
    /// <param name="reason">Why it was stopped. Recorded and returned on blocked checks.</param>
    public void StopPlatform(string platform, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform, nameof(platform));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(reason));
        lock (_gate)
        {
            _stoppedPlatforms[platform] = reason;
        }
    }

    /// <summary>Lifts a platform stop. An owner action; the caller is responsible for authorization.</summary>
    /// <param name="platform">The platform to resume.</param>
    public void ResumePlatform(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform, nameof(platform));
        lock (_gate)
        {
            _stoppedPlatforms.Remove(platform);
        }
    }

    /// <summary>Stops every community write by one agent, on every platform, until <see cref="ResumeAgent"/> is called.</summary>
    /// <param name="agentId">The agent to stop.</param>
    /// <param name="reason">Why it was stopped. Recorded and returned on blocked checks.</param>
    public void StopAgent(string agentId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId, nameof(agentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(reason));
        lock (_gate)
        {
            _stoppedAgents[agentId] = reason;
        }
    }

    /// <summary>Lifts an agent stop. An owner action; the caller is responsible for authorization.</summary>
    /// <param name="agentId">The agent to resume.</param>
    public void ResumeAgent(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId, nameof(agentId));
        lock (_gate)
        {
            _stoppedAgents.Remove(agentId);
        }
    }

    /// <summary>
    /// Records an objection or moderator ban: the target goes on the blocklist at once, and it is never reopened by code.
    /// </summary>
    /// <returns>A blocked decision with the target scope and the reason.</returns>
    public async Task<KillSwitchDecision> ObjectAsync(string platform, string targetRef, string reason, string addedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform, nameof(platform));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRef, nameof(targetRef));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason, nameof(reason));
        ArgumentException.ThrowIfNullOrWhiteSpace(addedBy, nameof(addedBy));

        await _blocklist.AddAsync(platform, targetRef, reason, addedBy, ct).ConfigureAwait(false);
        return new KillSwitchDecision(true, TargetScope, reason);
    }
}
