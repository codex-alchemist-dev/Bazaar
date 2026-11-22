using System.Net;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Pipeline;

namespace Bazaar.Server.Gateway.Catalogue.Tests;

public sealed class NativeListReaderTests
{
    private static GwProvider Provider(string id, string adapter, string baseUrl) =>
        new(id, "first_party", adapter, baseUrl, "api_key", "env:K", "{}", null, null, true);

    [Fact]
    public void Anthropic_page_reads_ids_and_the_cursor_only_when_more_follow()
    {
        Assert.True(ModelListParsers.TryParseAnthropic("{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}],\"has_more\":true,\"last_id\":\"b\"}", out var more));
        Assert.Equal(["a", "b"], more.Ids);
        Assert.Equal("b", more.NextCursor);

        Assert.True(ModelListParsers.TryParseAnthropic("{\"data\":[{\"id\":\"a\"}],\"has_more\":false,\"last_id\":\"a\"}", out var last));
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public void Gemini_page_strips_the_models_prefix_and_reads_the_token()
    {
        Assert.True(ModelListParsers.TryParseGemini("{\"models\":[{\"name\":\"models/gemini-x\"},{\"name\":\"other\"},{\"nope\":1}],\"nextPageToken\":\"t\"}", out var page));
        Assert.Equal(["gemini-x", "other"], page.Ids);
        Assert.Equal("t", page.NextCursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("[]")]
    [InlineData("{\"data\":1}")]
    public void Native_parsers_refuse_bodies_of_the_wrong_shape(string body)
    {
        Assert.False(ModelListParsers.TryParseAnthropic(body, out _));
        Assert.False(ModelListParsers.TryParseGemini(body, out _));
    }

    [Fact]
    public async Task Anthropic_listing_pages_and_sends_its_headers_not_a_bearer()
    {
        var handler = new SequenceHandler(
            "{\"data\":[{\"id\":\"c1\"}],\"has_more\":true,\"last_id\":\"c1\"}",
            "{\"data\":[{\"id\":\"c2\"}],\"has_more\":false}");
        var lister = new HttpProviderModelLister(new Store(Provider("anthropic", "anthropic_messages", "https://api.example.test")), new Secrets("sekret"), new HttpClient(handler));

        var ids = await lister.ListModelIdsAsync("anthropic", TestContext.Current.CancellationToken);

        Assert.Equal(["c1", "c2"], ids);
        Assert.Equal("https://api.example.test/v1/models?limit=1000", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("https://api.example.test/v1/models?limit=1000&after_id=c1", handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Equal("sekret", handler.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Requests[0].Headers.GetValues("anthropic-version").Single());
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Gemini_listing_keeps_the_key_out_of_the_url()
    {
        var handler = new SequenceHandler(
            "{\"models\":[{\"name\":\"models/g1\"}],\"nextPageToken\":\"tok 1\"}",
            "{\"models\":[{\"name\":\"models/g2\"}]}");
        var lister = new HttpProviderModelLister(new Store(Provider("gem", "gemini_generate_content", "https://gem.example.test/v1beta")), new Secrets("sekret"), new HttpClient(handler));

        var ids = await lister.ListModelIdsAsync("gem", TestContext.Current.CancellationToken);

        Assert.Equal(["g1", "g2"], ids);
        Assert.Equal("https://gem.example.test/v1beta/models?pageSize=1000&pageToken=tok%201", handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Equal("sekret", handler.Requests[0].Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("sekret", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cursor_that_never_ends_is_a_failed_read_not_a_partial_list()
    {
        var handler = new SequenceHandler("{\"data\":[{\"id\":\"x\"}],\"has_more\":true,\"last_id\":\"x\"}");
        var lister = new HttpProviderModelLister(new Store(Provider("a", "anthropic_messages", "https://api.example.test")), new Secrets("k"), new HttpClient(handler));

        Assert.Null(await lister.ListModelIdsAsync("a", TestContext.Current.CancellationToken));
        Assert.Equal(HttpProviderModelLister.MaxPages, handler.Requests.Count);
    }

    [Fact]
    public async Task A_failure_on_a_later_page_fails_the_whole_read()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK, "{\"models\":[{\"name\":\"models/a\"}],\"nextPageToken\":\"t\"}", HttpStatusCode.InternalServerError, "");
        var lister = new HttpProviderModelLister(new Store(Provider("g", "gemini_generate_content", "https://gem.example.test/v1beta")), new Secrets("k"), new HttpClient(handler));

        Assert.Null(await lister.ListModelIdsAsync("g", TestContext.Current.CancellationToken));
    }

    private sealed class Store(GwProvider provider) : ICatalogueStore
    {
        public Task<IReadOnlyList<GwProvider>> ListProvidersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GwProvider>>([provider]);

        public Task<IReadOnlyList<GwModel>> ListModelsAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GwModel>>([]);

        public Task UpsertModelAsync(GwModel model, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Secrets(string value) : IProviderSecretSource
    {
        public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct) => ValueTask.FromResult<string?>(value);
    }

    /// <summary>Answers the given bodies in order (status 200), repeating the last one; or, with explicit status/body pairs, those in order.</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly List<(HttpStatusCode Status, string Body)> _responses = [];

        public SequenceHandler(params string[] bodies)
        {
            foreach (var body in bodies)
            {
                _responses.Add((HttpStatusCode.OK, body));
            }
        }

        public SequenceHandler(HttpStatusCode s1, string b1, HttpStatusCode s2, string b2)
        {
            _responses.Add((s1, b1));
            _responses.Add((s2, b2));
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = _responses[Math.Min(Requests.Count, _responses.Count - 1)];
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
