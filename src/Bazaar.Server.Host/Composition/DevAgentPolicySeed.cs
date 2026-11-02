using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Dev-only: grants each configured responder agent the FreeLLMAPI default route and its concrete model at start, so a local
/// run can answer. Runs only when FreeLLMAPI is enabled. Config alone grants nothing; this writes the same policy row an
/// operator would, under the changed-by label <c>dev-seed</c>, so the audit shows where it came from.
/// </summary>
public sealed class DevAgentPolicySeed(
    IOptions<FreeLlmApiOptions> freeLlmApi,
    IOptions<AgentResponderOptions> responder,
    AgentModelPolicyRepository policies,
    ILogger<DevAgentPolicySeed> logger) : IHostedService
{
    /// <summary>The changed-by label written with each seeded row.</summary>
    public const string ChangedBy = "dev-seed";

    /// <summary>Policy entries for one agent: the default route name and its concrete model, provider-qualified.</summary>
    public static IReadOnlyList<string> EntriesFor(FreeLlmApiOptions options) =>
        [options.RouteName, $"{FreeLlmApiProvider.ProviderId}:{options.DefaultModel}"];

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = freeLlmApi.Value;
        if (!options.Enabled)
        {
            return;
        }

        foreach (var persona in responder.Value.Agents)
        {
            await policies.SaveAsync(
                persona.AgentId.ToString("D"),
                EntriesFor(options),
                options.RouteName,
                ChangedBy,
                cancellationToken).ConfigureAwait(false);
            Log.Seeded(logger, persona.AgentId, options.RouteName);
        }
    }

    private static class Log
    {
        private static readonly Action<ILogger, Guid, string, Exception?> _seeded = LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(1, nameof(Seeded)),
            "Dev policy seed: agent {AgentId} may use route {Route}.");

        public static void Seeded(ILogger logger, Guid agentId, string route) => _seeded(logger, agentId, route, null);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
