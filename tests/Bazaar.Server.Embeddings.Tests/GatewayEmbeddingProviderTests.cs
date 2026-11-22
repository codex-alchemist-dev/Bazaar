using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gateway.Fakes;
using GatewayEmbeddingResult = Bazaar.Contracts.Gateway.EmbeddingResult;

namespace Bazaar.Server.Embeddings.Tests;

public class GatewayEmbeddingProviderTests
{
    private static EmbeddingProviderOptions Options(int dims = 32) =>
        new() { Id = "gw", Kind = EmbeddingProviderKind.Gateway, ModelId = "embed-model", Dimensions = dims };

    [Fact]
    public async Task Embeds_through_the_gateway_with_the_system_agent_and_no_route()
    {
        var gateway = new FakeModelGateway(new FakeProvider());
        var provider = new GatewayEmbeddingProvider(gateway, Options());

        var result = await provider.EmbedAsync(["alpha", "beta"], CancellationToken.None);

        Assert.Equal(2, result.Vectors.Count);
        Assert.All(result.Vectors, v => Assert.Equal(32, v.Length));
        Assert.Equal(FakeModelGateway.Embed("alpha", 32), result.Vectors[0]);
        var request = Assert.Single(gateway.EmbeddingRequests);
        Assert.Equal("bazaar-embeddings", request.AgentId);
        Assert.Null(request.Route);
        Assert.Equal(["alpha", "beta"], request.Inputs);
        Assert.Equal(32, request.Dimensions);
    }

    [Fact]
    public async Task A_large_batch_is_passed_whole_without_chunking_here()
    {
        var gateway = new FakeModelGateway(new FakeProvider());
        var provider = new GatewayEmbeddingProvider(gateway, Options());
        var texts = Enumerable.Range(0, 5000).Select(i => $"t{i}").ToArray();

        var result = await provider.EmbedAsync(texts, CancellationToken.None);

        Assert.Equal(5000, result.Vectors.Count);
        Assert.Equal(5000, Assert.Single(gateway.EmbeddingRequests).Inputs.Count);
    }

    [Fact]
    public async Task An_empty_batch_makes_no_gateway_call()
    {
        var gateway = new FakeModelGateway(new FakeProvider());

        var result = await new GatewayEmbeddingProvider(gateway, Options()).EmbedAsync([], CancellationToken.None);

        Assert.Empty(result.Vectors);
        Assert.Empty(gateway.EmbeddingRequests);
    }

    [Fact]
    public async Task A_vector_of_the_wrong_length_is_a_profile_mismatch()
    {
        // The fake honours the requested dimension, so a provider configured for 16 against a gateway that returns 32 needs a stub.
        var provider = new GatewayEmbeddingProvider(new StubGateway(_ => new GatewayEmbeddingResult([new float[32]], "m", 1)), Options(16));

        await Assert.ThrowsAsync<EmbeddingProfileMismatchException>(() => provider.EmbedAsync(["x"], CancellationToken.None));
    }

    [Theory]
    [InlineData("embedding_route_unset", typeof(EmbeddingRetryLaterException))]
    [InlineData("policy_denied", typeof(EmbeddingRetryLaterException))]
    [InlineData("no_capacity", typeof(EmbeddingRetryLaterException))]
    [InlineData("embedding_input_too_long", typeof(EmbeddingInputTooLongException))]
    [InlineData("embedding_dimension_mismatch", typeof(EmbeddingProfileMismatchException))]
    [InlineData("upstream_error", typeof(EmbeddingException))]
    public async Task Gateway_failure_codes_map_to_embedding_exceptions(string code, Type expected)
    {
        var provider = new GatewayEmbeddingProvider(new StubGateway(_ => throw new GatewayModelException(code, "boom", 7)), Options());

        var ex = await Assert.ThrowsAnyAsync<EmbeddingException>(() => provider.EmbedAsync(["x"], CancellationToken.None));

        Assert.Equal(expected, ex.GetType());
        if (ex is EmbeddingRetryLaterException retry)
        {
            Assert.Equal(7, retry.RetryAfterSeconds);
        }
    }

    [Fact]
    public void A_blank_agent_id_is_rejected()
    {
        var options = Options();
        options.GatewayAgentId = " ";

        Assert.Throws<ArgumentException>(() => new GatewayEmbeddingProvider(new FakeModelGateway(new FakeProvider()), options));
    }

    private sealed class StubGateway(Func<EmbeddingRequest, GatewayEmbeddingResult> embed) : IModelGateway
    {
        public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GatewayEmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(embed(request));
    }
}
