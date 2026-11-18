namespace Bazaar.Secrets;

/// <summary>Who is asking the store. Only host components may read or write; agent callers are always denied (doc 14).</summary>
public abstract record SecretCaller;

/// <summary>A host-side component, for example the gateway or the git provider.</summary>
public sealed record HostCaller : SecretCaller
{
    public HostCaller(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        Component = component;
    }

    public string Component { get; }
}

/// <summary>An agent acting through its own identity. Denied by the store on every operation.</summary>
public sealed record AgentCaller : SecretCaller
{
    public AgentCaller(AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Agent = agent;
    }

    public AgentId Agent { get; }
}
