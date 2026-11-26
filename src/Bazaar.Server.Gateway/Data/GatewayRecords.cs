using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Gateway.Data;

/// <summary>A row of <c>gw_provider</c>. <c>AuthKind</c> is kept as text so a bad stored value can be refused rather than lost.</summary>
public sealed record GwProvider(
    string Id,
    string Kind,
    string Adapter,
    string BaseUrl,
    string AuthKind,
    string? SecretRef,
    string HeadersJson,
    string? ProxyRef,
    DateTimeOffset? TermsAckAt,
    bool Enabled);

/// <summary>
/// A row of <c>gw_model</c>. <c>Family</c> is the model-family class that fallbacks may not leave. <c>Kind</c> is
/// <see cref="KindChat"/> or <see cref="KindEmbedding"/>. <c>EmbedMaxBatch</c> and <c>EmbedMaxInputChars</c> are the model's
/// embedding caps; null means the provider default.
/// </summary>
public sealed record GwModel(
    string ProviderId,
    string ModelId,
    string Family,
    GatewayIdentity Identity,
    string CapsJson,
    string Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string Source,
    string Kind = GwModel.KindChat,
    int? EmbedMaxBatch = null,
    int? EmbedMaxInputChars = null)
{
    public const string KindChat = "chat";
    public const string KindEmbedding = "embedding";
    public const string StatusAvailable = "available";
    public const string StatusMissing = "missing";

    public ModelRef Ref => new(ProviderId, ModelId);
}

/// <summary>Which kind of model a call needs. Chat routes use only <c>chat</c> models, embedding routes only <c>embedding</c> models.</summary>
public enum GwModelKind
{
    Chat,
    Embedding,
}

/// <summary>How a route orders its candidates.</summary>
public enum RouteStrategy
{
    Priority,
    RoundRobin,
    LeastLoaded,
}

/// <summary>
/// A row of <c>gw_route</c>. For a delegated (<c>custom/*</c>) route the members name the upstream profile and
/// <c>ExpectedSet</c> lists the models Bazaar expects it to serve; the upstream picks the model.
/// </summary>
public sealed record GwRoute(string Id, RouteStrategy Strategy, IReadOnlyList<ModelRef> Members, IReadOnlyList<string> ExpectedSet);

/// <summary>A row of <c>gw_quota_state</c>: one provider key and model class, never one agent.</summary>
public sealed record GwQuotaState(
    string ProviderId,
    string KeyRef,
    string ModelClass,
    QuotaState State,
    DateTimeOffset? Until,
    string ObservedJson);

/// <summary>The stored form of an agent policy (<c>agent_model_policy</c>), with who changed it and when.</summary>
public sealed record AgentModelPolicyRow(
    string AgentId,
    IReadOnlyList<string> Allowed,
    string? DefaultRoute,
    long Version,
    string ChangedBy,
    DateTimeOffset ChangedAt);
