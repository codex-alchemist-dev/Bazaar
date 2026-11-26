using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Policy;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.ClaudeLane;

/// <summary>
/// Operator settings for the Claude lane route (docs/design/05-model-gateway.md "Claude lane", docs/design/92-endpoint-selection.md).
/// Off by default. When on, one route (default <c>claude</c>) holds every Claude endpoint the install has: the owner's Anthropic
/// key (identity <c>native</c>) and FreeLLMAPI's Claude entries (identity <c>pooled</c>). The selector orders them, with the
/// preferred identity as a hard tier, so FreeLLMAPI serves when the owner's key is blocked, down or capped.
/// </summary>
public sealed class ClaudeLaneOptions
{
    public const string SectionName = "Bazaar:Gateway:ClaudeLane";

    public bool Enabled { get; set; }

    /// <summary>The route name agents are given. Must be a valid route name.</summary>
    public string RouteName { get; set; } = "claude";

    /// <summary>The selection profile bound to the route (doc 92). Default <c>balanced</c>.</summary>
    public string Profile { get; set; } = "balanced";

    /// <summary>Doc 05 <c>gateway.claude.order</c>: true is <c>native_first</c> (default), false is <c>pooled_first</c>.</summary>
    public bool NativeFirst { get; set; } = true;

    /// <summary>
    /// Model ids FreeLLMAPI serves from its Claude pool, listed by the operator. Each becomes <c>freellmapi:&lt;id&gt;</c> with
    /// family <c>claude</c> and identity <c>pooled</c>. Empty means the lane has no pooled fallback. Only used when FreeLLMAPI is on.
    /// </summary>
    public List<string> PooledModels { get; set; } = [];

    public IReadOnlyList<string> EffectivePooledModels() =>
        [.. PooledModels.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.Ordinal)];
}

public sealed class ClaudeLaneOptionsValidator : IValidateOptions<ClaudeLaneOptions>
{
    public ValidateOptionsResult Validate(string? name, ClaudeLaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var section = ClaudeLaneOptions.SectionName;
        var failures = new List<string>();
        if (!RouteNames.IsValid(options.RouteName) || RouteNames.IsDelegated(options.RouteName))
        {
            failures.Add($"{section}:RouteName must be a plain route name (not auto/* or custom/*).");
        }

        if (string.IsNullOrWhiteSpace(options.Profile))
        {
            failures.Add($"{section}:Profile is required.");
        }

        foreach (var model in options.EffectivePooledModels())
        {
            if (!ModelRef.TryParse($"{FreeLlmApiProvider.ProviderId}:{model}", out _))
            {
                failures.Add($"{section}:PooledModels entry '{model}' is not a usable model id.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Overlays the Claude lane route on another directory. Members are the Anthropic models when that source is on, then the pooled
/// FreeLLMAPI models when that source is on. Pooled models are given family <c>claude</c> here, so the doc 05 same-family rule keeps
/// them in one route with the native ones; every other lookup goes to the inner directory. The route is a plain priority route:
/// the selector (doc 92) decides the order, and the policy still decides who may use it.
/// </summary>
public sealed class ClaudeLaneGatewayDirectory : IGatewayDirectory
{
    private readonly IGatewayDirectory _inner;
    private readonly GwRoute? _route;
    private readonly Dictionary<ModelRef, GwModel> _pooled = [];

    public ClaudeLaneGatewayDirectory(IGatewayDirectory inner, ClaudeLaneOptions lane, AnthropicDirectOptions anthropic, FreeLlmApiOptions freeLlmApi, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(anthropic);
        ArgumentNullException.ThrowIfNull(freeLlmApi);
        ArgumentNullException.ThrowIfNull(time);
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        var members = new List<ModelRef>();
        if (anthropic.Enabled)
        {
            members.AddRange(anthropic.EffectiveModels().Select(m => new ModelRef(AnthropicDirectProvider.ProviderId, m)));
        }

        if (freeLlmApi.Enabled)
        {
            var now = time.GetUtcNow();
            foreach (var id in lane.EffectivePooledModels())
            {
                var member = new ModelRef(FreeLlmApiProvider.ProviderId, id);
                _pooled[member] = new GwModel(member.ProviderId, member.ModelId, AnthropicDirectProvider.Family, GatewayIdentity.Pooled, "{}", GwModel.StatusAvailable, now, now, "config");
                members.Add(member);
            }
        }

        _route = members.Count == 0 ? null : new GwRoute(lane.RouteName, RouteStrategy.Priority, members, []);
    }

    /// <summary>The route's members, for tests and the admin panel.</summary>
    public IReadOnlyList<ModelRef> Members => _route?.Members ?? [];

    public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) =>
        _inner.GetProviderAsync(providerId, cancellationToken);

    public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) =>
        _pooled.TryGetValue(model, out var found) ? ValueTask.FromResult<GwModel?>(found) : _inner.GetModelAsync(model, cancellationToken);

    public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) =>
        _route is not null && routeId == _route.Id ? ValueTask.FromResult<GwRoute?>(_route) : _inner.GetRouteAsync(routeId, cancellationToken);
}
