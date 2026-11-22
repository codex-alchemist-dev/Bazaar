using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Ai;

namespace Bazaar.Server.Embeddings;

/// <summary>
/// Embeddings through the model gateway (doc 05, Embeddings), so per-agent policy, health, quota and usage records apply. The call goes to
/// <see cref="IModelGateway.EmbedAsync"/> with no route, so the gateway uses the install's <c>gateway.embedding.route</c>. The gateway
/// splits batches by the model's caps and merges the results in order, so this provider does no chunking and takes no route of its own.
/// Gateway failure codes become embedding exceptions: <c>embedding_route_unset</c>, <c>policy_denied</c> and <c>no_capacity</c> are
/// <see cref="EmbeddingRetryLaterException"/>, <c>embedding_input_too_long</c> is <see cref="EmbeddingInputTooLongException"/>,
/// <c>embedding_dimension_mismatch</c> is <see cref="EmbeddingProfileMismatchException"/>, and any other code is a plain
/// <see cref="EmbeddingException"/>.
/// </summary>
public sealed class GatewayEmbeddingProvider : IEmbeddingProvider
{
    public const string RouteUnsetCode = "embedding_route_unset";
    public const string InputTooLongCode = "embedding_input_too_long";
    public const string DimensionMismatchCode = "embedding_dimension_mismatch";
    public const string PolicyDeniedCode = "policy_denied";
    public const string NoCapacityCode = "no_capacity";

    private readonly IModelGateway _gateway;
    private readonly string _agentId;

    public GatewayEmbeddingProvider(IModelGateway gateway, EmbeddingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.GatewayAgentId))
        {
            throw new ArgumentException("A gateway agent id is required.", nameof(options));
        }

        _gateway = gateway;
        _agentId = options.GatewayAgentId;
        Descriptor = new EmbeddingProviderDescriptor(EmbeddingProviderKind.Gateway, options.ModelId, options.Dimensions);
    }

    public EmbeddingProviderDescriptor Descriptor { get; }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return new EmbeddingResult([]);
        }

        Bazaar.Contracts.Gateway.EmbeddingResult reply;
        try
        {
            reply = await _gateway.EmbedAsync(new EmbeddingRequest(_agentId, Route: null, texts, Descriptor.Dimensions), cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayModelException ex)
        {
            throw Map(ex);
        }

        // The gateway already fails closed on a dimension mismatch against the install profile; this guards the provider's own contract.
        if (reply.Vectors.Count != texts.Count || reply.Vectors.Any(v => v.Length != Descriptor.Dimensions))
        {
            throw new EmbeddingProfileMismatchException("The gateway returned embeddings with the wrong count or dimension.");
        }

        return new EmbeddingResult(reply.Vectors);
    }

    private static EmbeddingException Map(GatewayModelException ex) => ex.Code switch
    {
        RouteUnsetCode => new EmbeddingRetryLaterException(
            "The gateway has no embedding route: set gateway.embedding.route.", ex.RetryAfterSeconds, ex),
        PolicyDeniedCode => new EmbeddingRetryLaterException(
            $"The gateway's policy denies the embedding call: {ex.Message}", ex.RetryAfterSeconds, ex),
        NoCapacityCode => new EmbeddingRetryLaterException(
            $"The gateway has no capacity for the embedding call: {ex.Message}", ex.RetryAfterSeconds, ex),
        InputTooLongCode => new EmbeddingInputTooLongException($"An input is too long for the embedding model: {ex.Message}", ex),
        DimensionMismatchCode => new EmbeddingProfileMismatchException($"The gateway's vectors do not fit the embedding profile: {ex.Message}"),
        _ => new EmbeddingException($"The gateway failed the embedding call ({ex.Code}): {ex.Message}", ex),
    };
}
