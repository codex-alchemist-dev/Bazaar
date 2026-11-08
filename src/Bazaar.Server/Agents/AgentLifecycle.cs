namespace Bazaar.Server.Agents;

/// <summary>The allowed agent lifecycle moves (doc 06, "Lifecycle"). Retired is terminal.</summary>
public static class AgentLifecycle
{
    /// <summary>True when an agent may move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool CanMove(AgentState from, AgentState to) => (from, to) switch
    {
        (AgentState.Created, AgentState.Onboarding) => true,
        (AgentState.Onboarding, AgentState.Active) => true,
        (AgentState.Active, AgentState.Resting) => true,
        (AgentState.Resting, AgentState.Active) => true,
        (_, AgentState.Retired) => from != AgentState.Retired,
        _ => false,
    };
}
