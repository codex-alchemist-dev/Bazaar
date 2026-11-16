namespace Bazaar.Secrets;

/// <summary>
/// Where a secret lives: the installation (provider and App keys) or one agent's private scope.
/// Each scope has its own data key, so crypto-erasing one agent leaves every other scope readable.
/// </summary>
public sealed record SecretScope
{
    private SecretScope(AgentId? agent)
    {
        Agent = agent;
    }

    public static SecretScope Installation { get; } = new((AgentId?)null);

    public AgentId? Agent { get; }

    /// <summary>Stable key used in storage and audit. Never a secret.</summary>
    public string Key => Agent is null ? "installation" : "agent:" + Agent.Value;

    public static SecretScope ForAgent(AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return new SecretScope(agent);
    }
}
