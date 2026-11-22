using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// The config-defined FreeLLMAPI provider, model and route. They live in memory, built from <see cref="FreeLlmApiOptions"/>,
/// so no migration or database seed is needed and a disabled config leaves no row behind.
/// </summary>
public static class FreeLlmApiProvider
{
    /// <summary>The provider id. Model refs read <c>freellmapi:&lt;model&gt;</c>.</summary>
    public const string ProviderId = "freellmapi";

    /// <summary>FreeLLMAPI speaks OpenAI chat, so it uses the openai_chat adapter (the name the adapter registry keys on).</summary>
    public const string AdapterName = "openai_chat";

    /// <summary>
    /// The provider's secret reference. It has the <c>secret://bazaar/providers/</c> shape that <see cref="CredentialKinds"/>
    /// requires, and <see cref="FreeLlmApiSecretSource"/> resolves it from the configured <c>env:</c> or <c>file:</c> reference.
    /// </summary>
    public const string SecretRef = "secret://bazaar/providers/freellmapi/api-key";

    /// <summary>The model family class. One FreeLLMAPI model is the only member of its route, so it never mixes families.</summary>
    public const string Family = "freellmapi";
}

/// <summary>
/// Overlays the config-defined FreeLLMAPI provider, model and route on the database directory. Lookups for anything else
/// go to the inner directory unchanged. When the config names the route, the config route wins over a stored route of the same name.
/// </summary>
public sealed class FreeLlmApiGatewayDirectory : IGatewayDirectory
{
    private readonly IGatewayDirectory _inner;
    private readonly GwProvider _provider;
    private readonly GwModel _model;
    private readonly GwRoute _route;

    /// <param name="options">Must be enabled and valid (the validator runs at start).</param>
    public FreeLlmApiGatewayDirectory(IGatewayDirectory inner, FreeLlmApiOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        var now = time.GetUtcNow();
        _provider = new GwProvider(
            FreeLlmApiProvider.ProviderId,
            Kind: "gateway",
            Adapter: FreeLlmApiProvider.AdapterName,
            BaseUrl: options.BaseUrl.TrimEnd('/'),
            AuthKind: "api_key",
            SecretRef: FreeLlmApiProvider.SecretRef,
            HeadersJson: "{}",
            ProxyRef: null,
            TermsAckAt: null,
            Enabled: true);
        var member = new ModelRef(FreeLlmApiProvider.ProviderId, options.DefaultModel);
        _model = new GwModel(
            member.ProviderId,
            member.ModelId,
            FreeLlmApiProvider.Family,
            GatewayIdentity.Pooled,
            "{}",
            GwModel.StatusAvailable,
            now,
            now,
            "config");
        _route = new GwRoute(options.RouteName, RouteStrategy.Priority, [member], []);
    }

    public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) =>
        providerId == FreeLlmApiProvider.ProviderId
            ? ValueTask.FromResult<GwProvider?>(_provider)
            : _inner.GetProviderAsync(providerId, cancellationToken);

    public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) =>
        model == _model.Ref
            ? ValueTask.FromResult<GwModel?>(_model)
            : _inner.GetModelAsync(model, cancellationToken);

    public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) =>
        routeId == _route.Id
            ? ValueTask.FromResult<GwRoute?>(_route)
            : _inner.GetRouteAsync(routeId, cancellationToken);
}
