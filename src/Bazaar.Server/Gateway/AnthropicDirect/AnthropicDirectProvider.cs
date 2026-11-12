using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Gateway.AnthropicDirect;

/// <summary>
/// The config-defined Anthropic provider, its models and their routes. They live in memory, built from
/// <see cref="AnthropicDirectOptions"/>, so no migration or database seed is needed and a disabled config leaves no row behind.
/// </summary>
public static class AnthropicDirectProvider
{
    /// <summary>The provider id (doc 05, "Claude lane"). Model refs read <c>anthropic-direct:&lt;model&gt;</c>.</summary>
    public const string ProviderId = "anthropic-direct";

    /// <summary>The adapter name the adapter registry keys on.</summary>
    public const string AdapterName = "anthropic_messages";

    /// <summary>
    /// The provider's secret reference. It has the <c>secret://bazaar/providers/</c> shape that <see cref="CredentialKinds"/>
    /// requires, and <see cref="AnthropicDirectSecretSource"/> resolves it from the configured <c>env:</c> or <c>file:</c> reference.
    /// </summary>
    public const string SecretRef = "secret://bazaar/providers/anthropic-direct/api-key";

    /// <summary>The model family class. Every model of this source is in it, so a fallback never leaves Claude.</summary>
    public const string Family = "claude";
}

/// <summary>
/// Overlays the config-defined Anthropic provider, models and routes on another directory. Lookups for anything else go to the
/// inner directory unchanged. A config route wins over a stored route of the same name. Models carry the <c>native</c> identity,
/// so their usage rows are labelled as the owner's own key.
/// </summary>
public sealed class AnthropicDirectGatewayDirectory : IGatewayDirectory
{
    private readonly IGatewayDirectory _inner;
    private readonly GwProvider _provider;
    private readonly Dictionary<ModelRef, GwModel> _models = [];
    private readonly Dictionary<string, GwRoute> _routes = new(StringComparer.Ordinal);

    /// <param name="options">Must be enabled and valid (the validator runs at start).</param>
    public AnthropicDirectGatewayDirectory(IGatewayDirectory inner, AnthropicDirectOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        var now = time.GetUtcNow();
        _provider = new GwProvider(
            AnthropicDirectProvider.ProviderId,
            Kind: "first_party",
            Adapter: AnthropicDirectProvider.AdapterName,
            BaseUrl: options.BaseUrl.TrimEnd('/'),
            AuthKind: "api_key",
            SecretRef: AnthropicDirectProvider.SecretRef,
            HeadersJson: "{}",
            ProxyRef: null,
            TermsAckAt: null,
            Enabled: true);

        foreach (var modelId in options.EffectiveModels())
        {
            var member = new ModelRef(AnthropicDirectProvider.ProviderId, modelId);
            _models[member] = new GwModel(
                member.ProviderId,
                member.ModelId,
                AnthropicDirectProvider.Family,
                GatewayIdentity.Native,
                "{}",
                GwModel.StatusAvailable,
                now,
                now,
                "config");
            _routes[modelId] = new GwRoute(modelId, RouteStrategy.Priority, [member], []);
        }
    }

    public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) =>
        providerId == AnthropicDirectProvider.ProviderId
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
