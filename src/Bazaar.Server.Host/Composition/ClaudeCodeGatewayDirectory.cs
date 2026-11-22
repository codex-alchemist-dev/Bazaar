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

    /// <summary>The route that runs the default model through the CLI, with the owner's Anthropic key as its same-family fallback.</summary>
    public const string DefaultRoute = "claude-code";

    /// <summary>The route for one model id, for example <c>claude-code/claude-sonnet-5-5</c>.</summary>
    public static string RouteFor(string modelId) => $"{DefaultRoute}/{modelId}";
}

/// <summary>
/// Overlays the config-defined Claude Code provider, its models and its routes on another directory. Nothing is stored, so a
/// disabled config leaves no row behind. The provider has no credential (<c>auth_kind</c> <c>none</c>): the CLI holds the
/// operator's own sign-in and Bazaar never sees it. Models carry the <c>subscription</c> identity, so usage is labelled and the
/// cost is notional. The default route lists the CLI first and the owner's Anthropic key second when that source offers the
/// same model, so an exhausted plan falls back inside the Claude family and never leaves it (doc 05).
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

            List<ModelRef> members = [member];
            if (anthropicModels.Contains(modelId, StringComparer.Ordinal))
            {
                members.Add(new ModelRef(AnthropicDirectProvider.ProviderId, modelId));
            }

            _routes[ClaudeCodeProvider.RouteFor(modelId)] = new GwRoute(ClaudeCodeProvider.RouteFor(modelId), RouteStrategy.Priority, members, []);
            if (modelId == options.EffectiveModels()[0])
            {
                _routes[ClaudeCodeProvider.DefaultRoute] = new GwRoute(ClaudeCodeProvider.DefaultRoute, RouteStrategy.Priority, members, []);
            }
        }
    }

    public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) =>
        providerId == ClaudeCodeProvider.ProviderId
            ? ValueTask.FromResult<GwProvider?>(_provider)
            : _inner.GetProviderAsync(providerId, cancellationToken);

    public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) =>
        _models.TryGetValue(model, out var found)
            ? ValueTask.FromResult<GwModel?>(found)
            : _inner.GetModelAsync(model, cancellationToken);

    public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) =>
        _routes.TryGetValue(routeId, out var found)
            ? ValueTask.FromResult<GwRoute?>(found)
            : _inner.GetRouteAsync(routeId, cancellationToken);
}
