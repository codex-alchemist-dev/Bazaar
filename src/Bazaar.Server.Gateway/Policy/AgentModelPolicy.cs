namespace Bazaar.Server.Gateway.Policy;

/// <summary>Raised when a policy or entry cannot be accepted. Policy parsing fails closed: a bad entry rejects the whole policy.</summary>
public sealed class PolicyFormatException(string message) : Exception(message);

/// <summary>
/// One agent's model whitelist (<c>agent_model_policy</c>). Deny by default: only what is listed is allowed.
/// Entries are provider-qualified models or named routes. A route name lets the agent use the route; the route's
/// candidates still have to be listed as models, so editing a shared route never widens an agent's whitelist.
/// </summary>
public sealed class AgentModelPolicy
{
    private readonly HashSet<ModelRef> _models;
    private readonly HashSet<string> _routes;

    private AgentModelPolicy(
        string agentId,
        IReadOnlyList<string> entries,
        HashSet<ModelRef> models,
        HashSet<string> routes,
        string? defaultRoute,
        long version)
    {
        AgentId = agentId;
        Entries = entries;
        _models = models;
        _routes = routes;
        DefaultRoute = defaultRoute;
        Version = version;
    }

    public string AgentId { get; }

    /// <summary>The entries as written, in order, for display and storage.</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>The route or model used when a request names none. Always one of the allowed entries.</summary>
    public string? DefaultRoute { get; }

    public long Version { get; }

    public IReadOnlyCollection<ModelRef> Models => _models;

    public bool AllowsModel(ModelRef model) => _models.Contains(model);

    public bool AllowsRoute(string route) => _routes.Contains(route);

    /// <summary>
    /// Builds a policy from stored entries. Rejects wildcards, <c>auto</c> routes, duplicates and a default route
    /// that is not itself allowed.
    /// </summary>
    public static AgentModelPolicy Create(string agentId, IEnumerable<string> entries, string? defaultRoute, long version)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new PolicyFormatException("A policy needs an agent id.");
        }

        ArgumentNullException.ThrowIfNull(entries);
        var written = new List<string>();
        var models = new HashSet<ModelRef>();
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (ModelRef.TryParse(entry, out var model))
            {
                if (!models.Add(model))
                {
                    throw new PolicyFormatException($"Duplicate policy entry '{entry}'.");
                }
            }
            else if (RouteNames.IsValid(entry))
            {
                if (!routes.Add(entry))
                {
                    throw new PolicyFormatException($"Duplicate policy entry '{entry}'.");
                }
            }
            else
            {
                throw new PolicyFormatException($"Policy entry '{entry}' is neither a provider-qualified model nor a valid route name; wildcards and auto routes are not allowed.");
            }

            written.Add(entry);
        }

        if (defaultRoute is not null)
        {
            var allowed = ModelRef.TryParse(defaultRoute, out var defaultModel) ? models.Contains(defaultModel) : routes.Contains(defaultRoute);
            if (!allowed)
            {
                throw new PolicyFormatException($"Default route '{defaultRoute}' is not one of the allowed entries.");
            }
        }

        return new AgentModelPolicy(agentId, written, models, routes, defaultRoute, version);
    }
}
