using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Embeddings.Tests;

public class FakeProviderTests
{
    [Fact]
    public async Task Same_text_gives_the_same_vector()
    {
        var provider = new FakeEmbeddingProvider(16);
        var a = await provider.EmbedAsync(["hello", "world"], CancellationToken.None);
        var b = await provider.EmbedAsync(["hello", "world"], CancellationToken.None);

        Assert.Equal(a.Vectors[0], b.Vectors[0]);
        Assert.Equal(a.Vectors[1], b.Vectors[1]);
        Assert.NotEqual(a.Vectors[0], a.Vectors[1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(1024)]
    public async Task Vectors_have_the_configured_dimension_and_unit_length(int dims)
    {
        var provider = new FakeEmbeddingProvider(dims);
        var result = await provider.EmbedAsync(["text"], CancellationToken.None);

        var vector = Assert.Single(result.Vectors);
        Assert.Equal(dims, vector.Length);
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(v => (double)v * v)), 4);
        Assert.Equal(dims, provider.Descriptor.Dimensions);
        Assert.Equal(EmbeddingProviderKind.Fake, provider.Descriptor.Kind);
    }

    [Fact]
    public void Dimension_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeEmbeddingProvider(0));
    }
}

public class OpenAiCompatibleProviderTests
{
    private static readonly float[] _expectedFirstValues = [1f, 2f, 3f, 4f, 5f];

    private static EmbeddingProviderOptions Options(string baseUrl = "http://localhost:11434", int batch = 2, string? key = null) => new()
    {
        Id = "local", Kind = EmbeddingProviderKind.OpenAiCompatible, ModelId = "nomic", Dimensions = 3, BaseUrl = baseUrl, BatchSize = batch, ApiKeyReference = key,
    };

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers with one vector [n, n, n] per input, in reverse order, to prove the reply is sorted by index.</summary>
    private static HttpResponseMessage Echo(HttpRequestMessage _, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var inputs = doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var items = inputs.Select((text, i) => $$"""{"index":{{i}},"embedding":[{{text.Length}},{{text.Length}},{{text.Length}}]}""").Reverse();
        return Reply($$"""{"data":[{{string.Join(',', items)}}]}""");
    }

    [Fact]
    public async Task Posts_to_v1_embeddings_in_batches_and_keeps_input_order()
    {
        var handler = new StubHandler(Echo);
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options());

        var result = await provider.EmbedAsync(["a", "bb", "ccc", "dddd", "eeeee"], CancellationToken.None);

        Assert.Equal(3, handler.Calls.Count);
        Assert.Equal("http://localhost:11434/v1/embeddings", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal(_expectedFirstValues, result.Vectors.Select(v => v[0]).ToArray());
        using var first = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.Equal("nomic", first.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("https://proxy.example/v1")]
    [InlineData("https://proxy.example/v1/")]
    public async Task A_base_url_ending_in_v1_gets_no_second_v1(string baseUrl)
    {
        var handler = new StubHandler(Echo);
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options(baseUrl));

        await provider.EmbedAsync(["x"], CancellationToken.None);

        Assert.Equal("https://proxy.example/v1/embeddings", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Sends_the_bearer_key_resolved_through_the_secret_resolver()
    {
        var handler = new StubHandler(Echo);
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options(key: "env:EMBED_KEY"), new StaticResolver("s3cret"));

        await provider.EmbedAsync(["x"], CancellationToken.None);

        Assert.Equal("Bearer", handler.Calls[0].Request.Headers.Authorization!.Scheme);
        Assert.Equal("s3cret", handler.Calls[0].Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task No_key_reference_means_no_authorization_header()
    {
        var handler = new StubHandler(Echo);
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options());

        await provider.EmbedAsync(["x"], CancellationToken.None);

        Assert.Null(handler.Calls[0].Request.Headers.Authorization);
    }

    [Fact]
    public async Task A_key_reference_with_no_resolver_fails_instead_of_sending_unauthenticated()
    {
        var handler = new StubHandler(Echo);
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options(key: "env:EMBED_KEY"));

        await Assert.ThrowsAsync<EmbeddingException>(() => provider.EmbedAsync(["x"], CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task A_wrong_dimension_is_refused()
    {
        var handler = new StubHandler((_, _) => Reply("""{"data":[{"index":0,"embedding":[1,2]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options());

        var ex = await Assert.ThrowsAsync<EmbeddingException>(() => provider.EmbedAsync(["x"], CancellationToken.None));
        Assert.Contains("dimensions", ex.Message);
    }

    [Fact]
    public async Task A_wrong_count_is_refused()
    {
        var handler = new StubHandler((_, _) => Reply("""{"data":[{"index":0,"embedding":[1,2,3]}]}"""));
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options());

        await Assert.ThrowsAsync<EmbeddingException>(() => provider.EmbedAsync(["x", "y"], CancellationToken.None));
    }

    [Fact]
    public async Task An_http_error_does_not_echo_the_response_body()
    {
        var handler = new StubHandler((_, _) => Reply("secret input text", HttpStatusCode.InternalServerError));
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options());

        var ex = await Assert.ThrowsAsync<EmbeddingException>(() => provider.EmbedAsync(["x"], CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain("secret input text", ex.Message);
    }

    [Fact]
    public async Task Malformed_json_becomes_an_embedding_exception()
    {
        var handler = new StubHandler((_, _) => Reply("not json"));
        var provider = new OpenAiCompatibleEmbeddingProvider(new HttpClient(handler), Options());

        await Assert.ThrowsAsync<EmbeddingException>(() => provider.EmbedAsync(["x"], CancellationToken.None));
    }

    [Fact]
    public void A_missing_base_url_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleEmbeddingProvider(new HttpClient(), new EmbeddingProviderOptions { Id = "x", ModelId = "m", Dimensions = 3 }));
    }

    private sealed class StaticResolver(string value) : ISecretResolver
    {
        public bool CanResolve(string reference) => true;

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }
}

public class RegistryTests
{
    [Fact]
    public void Builds_fake_and_openai_providers_by_id()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Providers =
            [
                new() { Id = "fake", Kind = EmbeddingProviderKind.Fake, Dimensions = 4 },
                new() { Id = "ollama", Kind = EmbeddingProviderKind.OpenAiCompatible, ModelId = "m", Dimensions = 4, BaseUrl = "http://localhost:11434" },
            ],
        });
        using var registry = new EmbeddingProviderRegistry(options);

        Assert.True(registry.TryGet("fake", out var fake));
        Assert.Equal(EmbeddingProviderKind.Fake, fake.Descriptor.Kind);
        Assert.True(registry.TryGet("ollama", out _));
        Assert.False(registry.TryGet("missing", out _));
    }

    [Fact]
    public void Gateway_kind_fails_clearly_while_the_gateway_has_no_embeddings_call()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Providers = [new() { Id = "gw", Kind = EmbeddingProviderKind.Gateway, ModelId = "m", Dimensions = 4 }],
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new EmbeddingProviderRegistry(options));
        Assert.Contains("Gateway", ex.Message);
    }

    [Fact]
    public void A_duplicate_id_is_rejected()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
        {
            Providers = [new() { Id = "a", Kind = EmbeddingProviderKind.Fake, Dimensions = 4 }, new() { Id = "a", Kind = EmbeddingProviderKind.Fake, Dimensions = 4 }],
        });

        Assert.Throws<InvalidOperationException>(() => new EmbeddingProviderRegistry(options));
    }
}
