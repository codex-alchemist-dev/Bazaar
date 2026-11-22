using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Server.Gateway.Policy;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Gateway.SystemPolicy;

/// <summary>
/// Declares the <c>bazaar-embeddings</c> system agent, which Memory uses to embed (doc 05, "System-agent policy seeding").
/// When <c>gateway.embedding.route</c> is a model id the policy is that model. When it is a route name the policy lists the
/// route and every member model the route names, because a route name alone does not widen the whitelist. The setting is the
/// default route. Empty means embeddings are not configured and nothing is declared.
/// </summary>
public sealed partial class EmbeddingsPolicySource(
    IEmbeddingRouteSource settings,
    IGatewayDirectory directory,
    ILogger<EmbeddingsPolicySource> logger) : ISystemAgentPolicySource
{
    /// <summary>The caller id Memory embeds as.</summary>
    public const string EmbeddingsAgentId = "bazaar-embeddings";

    public string AgentId => EmbeddingsAgentId;

    public async ValueTask<SystemAgentPolicyDeclaration?> DeclareAsync(CancellationToken cancellationToken = default)
    {
        var value = (await settings.GetEmbeddingRouteAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (value.Length == 0)
        {
            LogNotConfigured(logger, AgentId);
            return null;
        }

        if (ModelRef.TryParse(value, out _))
        {
            return new SystemAgentPolicyDeclaration(AgentId, [value], value);
        }

        var route = await directory.GetRouteAsync(value, cancellationToken).ConfigureAwait(false);
        if (route is null)
        {
            LogUnknownRoute(logger, AgentId, value);
            return null;
        }

        var entries = new List<string> { value };
        foreach (var member in route.Members)
        {
            var text = member.ToString();
            if (!entries.Contains(text, StringComparer.Ordinal))
            {
                entries.Add(text);
            }
        }

        return new SystemAgentPolicyDeclaration(AgentId, entries, value);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Embedding is not configured (gateway.embedding.route is empty); no policy row is written for {AgentId}.")]
    private static partial void LogNotConfigured(ILogger logger, string agentId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "gateway.embedding.route names '{Route}', which is not a known route; no policy row is written for {AgentId}.")]
    private static partial void LogUnknownRoute(ILogger logger, string agentId, string route);
}
