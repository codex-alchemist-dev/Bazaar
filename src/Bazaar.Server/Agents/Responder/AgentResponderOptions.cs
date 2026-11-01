namespace Bazaar.Server.Agents.Responder;

/// <summary>
/// Configuration for the agent responder, bound from the <c>Bazaar:AgentResponder</c> section. The agents it may answer as are
/// listed here until the Soul system (doc 07) provides per-agent records.
/// </summary>
public sealed class AgentResponderOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:AgentResponder";

    /// <summary>Messages loaded from the GC as chat history for one reply, the trigger included.</summary>
    public int HistoryLimit { get; init; } = 30;

    /// <summary>Largest page of new messages read per pass over one GC.</summary>
    public int ReadBatchSize { get; init; } = 100;

    /// <summary>The gateway route used when an agent does not name one. The route must exist in the gateway policy.</summary>
    public string DefaultRoute { get; init; } = "agent-default";

    /// <summary>The agents that may answer, with their prompts. An agent not listed here never replies.</summary>
    public List<AgentResponderPersona> Agents { get; init; } = [];

    /// <summary>Checks the limits and the persona list. Throws on the first problem.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is below 1.</exception>
    /// <exception cref="ArgumentException">A persona is incomplete or listed twice.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(HistoryLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReadBatchSize, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultRoute);

        var seen = new HashSet<Guid>();
        foreach (var persona in Agents)
        {
            ArgumentNullException.ThrowIfNull(persona);
            if (persona.AgentId == Guid.Empty)
                throw new ArgumentException("A responder persona needs an agent id.", nameof(Agents));
            ArgumentException.ThrowIfNullOrWhiteSpace(persona.Name, nameof(Agents));
            ArgumentException.ThrowIfNullOrWhiteSpace(persona.SystemPrompt, nameof(Agents));
            if (!seen.Add(persona.AgentId))
                throw new ArgumentException($"Agent {persona.AgentId:D} is listed more than once.", nameof(Agents));
        }
    }
}

/// <summary>One agent that may answer in GCs. A plain class, so configuration binding can fill it.</summary>
public sealed class AgentResponderPersona
{
    /// <summary>The agent's principal id, the same id it has as a GC member.</summary>
    public Guid AgentId { get; init; }

    /// <summary>The name shown in chat history for this agent.</summary>
    public string Name { get; init; } = "";

    /// <summary>The system prompt for every reply.</summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>The gateway route, or null for <see cref="AgentResponderOptions.DefaultRoute"/>.</summary>
    public string? Route { get; init; }
}
