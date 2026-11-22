using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Providers.ClaudeCode;

namespace Bazaar.Server.Host.Composition;

/// <summary>Names the Claude Code provider uses in the gateway directory (docs/design/91-claude-code-cli-provider.md section 6).</summary>
public static class ClaudeCodeProvider
{
    /// <summary>The provider id. Model refs read <c>claude-code-cli:&lt;model&gt;</c>.</summary>
    public const string ProviderId = "claude-code-cli";

    /// <summary>The provider row's base URL. The gateway requires one; the adapter ignores it and reads its own options.</summary>
    public const string Sentinel = "cli://claude-code";

    /// <summary>The model family class. Shared with the owner's Anthropic key, so a fallback between the two never leaves Claude.</summary>
    public const string Family = "claude";

    /// <summary>
    /// The Claude route: every Claude source the install offers, in priority order (the CLI's models first, then the owner's
    /// Anthropic key, then any stored members of a route with this name). An agent's policy still decides which of them it may use.
    /// </summary>
    public const string DefaultRoute = "claude";

    /// <summary>The route for one model id, for example <c>claude-code/claude-sonnet-5-5</c>.</summary>
    public static string RouteFor(string modelId) => $"{DefaultRoute}/{modelId}";
}

/// <summary>
/// Overlays the config-defined Claude Code provider, its models and its routes on another directory. Nothing is stored, so a
/// disabled config leaves no row behind. The provider has no credential (<c>auth_kind</c> <c>none</c>): the CLI holds the
/// operator's own sign-in and Bazaar never sees it. Models carry the <c>subscription</c> identity, so usage is labelled and the
/// cost is notional. The route <c>claude</c> lists every Claude source (the CLI first, then the owner's Anthropic key), so the
/// first agent draws from whichever is available and an exhausted plan falls back inside the Claude family and never leaves it
/// (doc 05). Members that another directory stores under the same route name are appended, so an operator can add a further
/// Claude source (a model row in the <c>claude</c> family) without code.
/// </summary>
public sealed class ClaudeCodeGatewayDirectory : IGatewayDirectory
{
    private readonly IGatewayDirectory _inner;
    private readonly GwProvider _provider;
    private readonly Dictionary<ModelRef, GwModel> _models = [];
    private readonly Dictionary<string, GwRoute> _routes = new(StringComparer.Ordinal);

    /// <param name="options">Must be enabled and valid (the validator runs at start).</param>
    /// <param name="anthropic">The owner's Anthropic key source, or null. Its model with the same id becomes the route's fallback.</param>
    public ClaudeCodeGatewayDirectory(IGatewayDirectory inner, ClaudeCodeOptions options, AnthropicDirectOptions? anthropic, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        var now = time.GetUtcNow();
        _provider = new GwProvider(
            ClaudeCodeProvider.ProviderId,
            Kind: "local",
            Adapter: ClaudeCodeAdapter.WireName,
            BaseUrl: ClaudeCodeProvider.Sentinel,
            AuthKind: "none",
            SecretRef: null,
            HeadersJson: "{}",
            ProxyRef: null,
            TermsAckAt: options.TermsAcknowledgedAt,
            Enabled: true);

        var anthropicModels = anthropic is { Enabled: true } ? anthropic.EffectiveModels() : [];
        List<ModelRef> all = [];
        foreach (var modelId in options.EffectiveModels())
        {
            var member = new ModelRef(ClaudeCodeProvider.ProviderId, modelId);
            _models[member] = new GwModel(
                member.ProviderId,
                member.ModelId,
                ClaudeCodeProvider.Family,
                GatewayIdentity.Subscription,
                "{}",
                GwModel.StatusAvailable,
                now,
                now,
                "config");
            all.Add(member);

            List<ModelRef> members = [member];
            if (anthropicModels.Contains(modelId, StringComparer.Ordinal))
            {
                members.Add(new ModelRef(AnthropicDirectProvider.ProviderId, modelId));
            }

            _routes[ClaudeCodeProvider.RouteFor(modelId)] = new GwRoute(ClaudeCodeProvider.RouteFor(modelId), RouteStrategy.Priority, members, []);
        }

        all.AddRange(anthropicModels.Select(m => new ModelRef(AnthropicDirectProvider.ProviderId, m)));
        _claude = all.Distinct().ToList();
    }

    private readonly List<ModelRef> _claude;

    public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) =>
        providerId == ClaudeCodeProvider.ProviderId
            ? ValueTask.FromResult<GwProvider?>(_provider)
            : _inner.GetProviderAsync(providerId, cancellationToken);

    public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) =>
        _models.TryGetValue(model, out var found)
            ? ValueTask.FromResult<GwModel?>(found)
            : _inner.GetModelAsync(model, cancellationToken);

    public async ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken)
    {
        if (routeId == ClaudeCodeProvider.DefaultRoute)
        {
            var stored = await _inner.GetRouteAsync(routeId, cancellationToken).ConfigureAwait(false);
            var members = _claude.Concat(stored?.Members ?? []).Distinct().ToList();
            return new GwRoute(routeId, RouteStrategy.Priority, members, []);
        }

        return _routes.TryGetValue(routeId, out var found) ? found : await _inner.GetRouteAsync(routeId, cancellationToken).ConfigureAwait(false);
    }
}
