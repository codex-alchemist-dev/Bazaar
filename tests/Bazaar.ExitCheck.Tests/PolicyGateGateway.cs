using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.ExitCheck.Tests;

/// <summary>
/// The gateway path the exit check needs: every call is resolved by the real GW7 <see cref="RouteResolver"/> first;
/// a denial becomes a <c>policy_denied</c> failure and nothing reaches the provider. Allowed calls go to the scripted
/// <see cref="FakeProvider"/>. Replace with the composed gateway when it exists (doc 05, pipeline).
/// </summary>
public sealed class PolicyGateGateway(RouteResolver resolver, FakeProvider provider) : IModelGateway
{
    /// <inheritdoc />
    public async IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolution = await resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (resolution is RouteResolution.Denied denied)
        {
            yield return new ModelEvent.Failure(PolicyDeniedCode, $"{denied.Reason}: {denied.Detail}");
            yield break;
        }

        await foreach (var modelEvent in provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return modelEvent;
        }
    }

    /// <summary>The failure code a denied call carries.</summary>
    public const string PolicyDeniedCode = "policy_denied";
}

/// <summary>In-memory policy and directory rows for the exit check: one agent that may use <c>fake:small</c> but not <c>fake:large</c>.</summary>
public sealed class ExitCheckPolicyWorld : IAgentPolicyStore, IGatewayDirectory
{
    /// <summary>The agent the exit check runs as.</summary>
    public const string AgentId = "charles";

    /// <summary>Allowed model.</summary>
    public const string AllowedModel = "fake:small";

    /// <summary>Exists, is healthy and is a real model, but is not on the agent's whitelist.</summary>
    public const string DeniedModel = "fake:large";

    private readonly AgentModelPolicy _policy = AgentModelPolicy.Create(AgentId, [AllowedModel], AllowedModel, 1);
    private readonly GwProvider _provider = new("fake", "first_party", "openai_chat", "https://example.invalid/v1", "api_key", "secret://bazaar/providers/fake/key", "{}", null, null, true);

    /// <summary>The resolver over this world.</summary>
    public RouteResolver Resolver => new(this, this);

    /// <inheritdoc />
    public ValueTask<AgentModelPolicy?> GetAsync(string agentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(agentId == AgentId ? _policy : null);

    /// <inheritdoc />
    public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(providerId == _provider.Id ? _provider : null);

    /// <inheritdoc />
    public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) =>
        ValueTask.FromResult<GwModel?>(model.ProviderId == "fake"
            ? new GwModel("fake", model.ModelId, "claude", GatewayIdentity.Native, "{}", GwModel.StatusAvailable, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test")
            : null);

    /// <inheritdoc />
    public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) => ValueTask.FromResult<GwRoute?>(null);
}
