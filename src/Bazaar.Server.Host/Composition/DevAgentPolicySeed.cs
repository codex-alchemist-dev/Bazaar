using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Providers.ClaudeCode;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Dev-only: grants each configured responder agent the FreeLLMAPI default route and its concrete model at start, so a local
/// run can answer. When the owner's Anthropic key is enabled (<c>Bazaar:Gateway:AnthropicDirect</c>) and offers the default
/// Haiku model, that one route and model are granted too; no other Anthropic model is ever seeded. Runs only when at least
/// one of the two sources is enabled, and only for the agents listed under <c>Bazaar:AgentResponder:Agents</c>; no other
/// agent's whitelist is touched. Config alone grants nothing; this writes the same policy row an operator would, under the
/// changed-by label <c>dev-seed</c>, so the audit shows where it came from.
/// </summary>
public sealed class DevAgentPolicySeed(
    IOptions<FreeLlmApiOptions> freeLlmApi,
    IOptions<AnthropicDirectOptions> anthropicDirect,
    IOptions<ClaudeCodeOptions> claudeCode,
    IOptions<AgentResponderOptions> responder,
    AgentModelPolicyRepository policies,
    ILogger<DevAgentPolicySeed> logger) : IHostedService
{
    /// <summary>The changed-by label written with each seeded row.</summary>
    public const string ChangedBy = "dev-seed";

    /// <summary>Policy entries for one agent: the default route name and its concrete model, provider-qualified.</summary>
    public static IReadOnlyList<string> EntriesFor(FreeLlmApiOptions options) =>
        [options.RouteName, $"{FreeLlmApiProvider.ProviderId}:{options.DefaultModel}"];

    /// <summary>
    /// Policy entries for the owner's Anthropic key: the default Haiku route and its provider-qualified model, or nothing
    /// when the source is off or does not offer that model. Other configured models stay an explicit owner edit (doc 05).
    /// </summary>
    public static IReadOnlyList<string> EntriesFor(AnthropicDirectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Enabled && options.EffectiveModels().Contains(AnthropicDirectOptions.DefaultModel, StringComparer.Ordinal)
            ? [AnthropicDirectOptions.DefaultModel, $"{AnthropicDirectProvider.ProviderId}:{AnthropicDirectOptions.DefaultModel}"]
            : [];
    }

    /// <summary>
    /// Policy entries for the Claude Code CLI provider: the <c>claude</c> route, the default model's route and every configured CLI
    /// model, provider-qualified, or nothing when the provider is off. The operator listed those models, so they are all granted;
    /// the Anthropic key's other models stay an explicit owner edit (doc 05, doc 91).
    /// </summary>
    public static IReadOnlyList<string> EntriesFor(ClaudeCodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return [];
        }

        var models = options.EffectiveModels();
        return
        [
            ClaudeCodeProvider.DefaultRoute,
            ClaudeCodeProvider.RouteFor(models[0]),
            .. models.Select(m => $"{ClaudeCodeProvider.ProviderId}:{m}"),
        ];
    }

    /// <summary>
    /// The whole seeded policy for one agent, or null when no source grants anything. The default route is <c>claude</c>
    /// when the Claude Code provider is on (every Claude source the agent may use, CLI first, then the owner's Anthropic key), otherwise the FreeLLMAPI
    /// route, otherwise the Anthropic Haiku route. FreeLLMAPI stays an allowed route beside it: the gateway never falls back
    /// across model families (doc 05), so that switch is a route choice, not an automatic failover.
    /// </summary>
    public static (IReadOnlyList<string> Entries, string DefaultRoute)? PolicyFor(
        FreeLlmApiOptions freeLlmApi,
        AnthropicDirectOptions anthropicDirect,
        ClaudeCodeOptions? claudeCode = null)
    {
        ArgumentNullException.ThrowIfNull(freeLlmApi);
        ArgumentNullException.ThrowIfNull(anthropicDirect);

        IReadOnlyList<string> pooled = freeLlmApi.Enabled ? EntriesFor(freeLlmApi) : [];
        var native = EntriesFor(anthropicDirect);
        var cli = claudeCode is null ? [] : EntriesFor(claudeCode);
        var entries = cli.Concat(pooled).Concat(native).Distinct(StringComparer.Ordinal).ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        var defaultRoute = cli.Count > 0
            ? ClaudeCodeProvider.DefaultRoute
            : freeLlmApi.Enabled ? freeLlmApi.RouteName : AnthropicDirectOptions.DefaultModel;
        return (entries, defaultRoute);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (PolicyFor(freeLlmApi.Value, anthropicDirect.Value, claudeCode.Value) is not { } policy)
        {
            return;
        }

        foreach (var persona in responder.Value.Agents)
        {
            await policies.SaveAsync(
                persona.AgentId.ToString("D"),
                policy.Entries,
                policy.DefaultRoute,
                ChangedBy,
                cancellationToken).ConfigureAwait(false);
            Log.Seeded(logger, persona.AgentId, string.Join(", ", policy.Entries));
        }
    }

    private static class Log
    {
        private static readonly Action<ILogger, Guid, string, Exception?> _seeded = LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(1, nameof(Seeded)),
            "Dev policy seed: agent {AgentId} may use {Entries}.");

        public static void Seeded(ILogger logger, Guid agentId, string entries) => _seeded(logger, agentId, entries, null);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
