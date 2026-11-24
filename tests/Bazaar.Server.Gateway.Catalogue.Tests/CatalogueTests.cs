using System.Net;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Pipeline;

namespace Bazaar.Server.Gateway.Catalogue.Tests;

public sealed class CatalogueTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static GwModel Model(string id, string status = GwModel.StatusAvailable, DateTimeOffset? seen = null) =>
        new("p", id, "fam", GatewayIdentity.Pooled, "{}", status, _t0, seen ?? _t0, "config");

    private static GwProvider Provider(string id = "p", bool enabled = true, string adapter = "openai_chat") =>
        new(id, "reverse_proxy", adapter, "https://example.test/v1", "api_key", "env:KEY", "{}", null, null, enabled);

    [Fact]
    public void Diff_splits_added_missing_returned_and_unchanged()
    {
        var known = new[] { Model("a"), Model("b"), Model("c", GwModel.StatusMissing), Model("d", GwModel.StatusMissing) };

        var diff = CatalogueDiffer.Diff(known, ["a", "c", "e", "e", " "]);

        Assert.Equal(["e"], diff.Added);
        Assert.Equal(["b"], diff.Missing);
        Assert.Equal(["c"], diff.Returned);
        Assert.Equal(["a"], diff.Unchanged);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void Diff_is_empty_when_nothing_changed()
    {
        var diff = CatalogueDiffer.Diff([Model("a"), Model("z", GwModel.StatusMissing)], ["a"]);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Diff_ids_are_case_sensitive()
    {
        var diff = CatalogueDiffer.Diff([Model("Gpt")], ["gpt"]);

        Assert.Equal(["gpt"], diff.Added);
        Assert.Equal(["Gpt"], diff.Missing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"data\": 3}")]
    [InlineData("{\"other\": []}")]
    public void Parser_refuses_bodies_that_are_not_a_model_list(string body)
    {
        Assert.False(OpenAiModelListParser.TryParse(body, out var ids));
        Assert.Empty(ids);
    }

    [Fact]
    public void Parser_reads_ids_and_skips_rows_without_one()
    {
        var ok = OpenAiModelListParser.TryParse("{\"data\":[{\"id\":\"a\"},{\"id\":3},{},{\"id\":\" \"},{\"id\":\"b\"},\"x\"]}", out var ids);

        Assert.True(ok);
        Assert.Equal(["a", "b"], ids);
    }

    [Fact]
    public async Task Refresh_marks_vanished_models_missing_revives_returned_and_adds_new_ones()
    {
        var store = new FakeStore([Provider()], [Model("a"), Model("b"), Model("c", GwModel.StatusMissing, _t0.AddDays(-5))]);
        var refresher = new CatalogueRefresher(store, new FakeLister(["a", "c", "n"]), new FixedTime(_now));

        var results = await refresher.RefreshAllAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(CatalogueRefreshOutcome.Refreshed, result.Outcome);
        Assert.Equal(GwModel.StatusMissing, store.Models["b"].Status);
        Assert.Equal(GwModel.StatusAvailable, store.Models["c"].Status);
        Assert.Equal(_now, store.Models["c"].LastSeen);
        Assert.Equal(_now, store.Models["a"].LastSeen);
        Assert.Equal(_t0, store.Models["b"].LastSeen);
        var added = store.Models["n"];
        Assert.Equal(GwModel.StatusAvailable, added.Status);
        Assert.Equal("fam", added.Family);
        Assert.Equal(GatewayIdentity.Pooled, added.Identity);
        Assert.Equal("catalogue", added.Source);
        Assert.Equal(_now, added.FirstSeen);
        Assert.Empty(result.Unclassified);
    }

    [Fact]
    public async Task A_failed_list_call_changes_nothing()
    {
        var store = new FakeStore([Provider()], [Model("a")]);
        var refresher = new CatalogueRefresher(store, new FakeLister(null), new FixedTime(_now));

        var result = await refresher.RefreshProviderAsync("p", TestContext.Current.CancellationToken);

        Assert.Equal(CatalogueRefreshOutcome.ListFailed, result.Outcome);
        Assert.Equal(0, store.Writes);
        Assert.Equal(GwModel.StatusAvailable, store.Models["a"].Status);
    }

    [Fact]
    public async Task An_empty_but_readable_list_marks_everything_missing()
    {
        var store = new FakeStore([Provider()], [Model("a"), Model("b")]);
        var refresher = new CatalogueRefresher(store, new FakeLister([]), new FixedTime(_now));

        await refresher.RefreshAllAsync(TestContext.Current.CancellationToken);

        Assert.All(store.Models.Values, m => Assert.Equal(GwModel.StatusMissing, m.Status));
    }

    [Fact]
    public async Task A_provider_with_no_model_to_copy_from_reports_unclassified_and_inserts_nothing()
    {
        var store = new FakeStore([Provider()], []);
        var refresher = new CatalogueRefresher(store, new FakeLister(["x", "y"]), new FixedTime(_now));

        var result = Assert.Single(await refresher.RefreshAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["x", "y"], result.Unclassified);
        Assert.Empty(store.Models);
    }

    [Fact]
    public async Task Disabled_and_unknown_providers_are_skipped()
    {
        var store = new FakeStore([Provider("p", enabled: false)], [Model("a")]);
        var refresher = new CatalogueRefresher(store, new FakeLister(["a"]), new FixedTime(_now));

        var disabled = await refresher.RefreshProviderAsync("p", TestContext.Current.CancellationToken);
        var unknown = await refresher.RefreshProviderAsync("nope", TestContext.Current.CancellationToken);

        Assert.Equal(CatalogueRefreshOutcome.Skipped, disabled.Outcome);
        Assert.Equal(CatalogueRefreshOutcome.Skipped, unknown.Outcome);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Refresh_never_touches_policy_or_routes()
    {
        // The store has no policy or route surface at all; the refresher only writes gw_model rows.
        Assert.Equal(["ListModelsAsync", "ListProvidersAsync", "UpsertModelAsync"], typeof(ICatalogueStore).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Lister_reads_an_openai_list_with_the_bearer_key()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{\"data\":[{\"id\":\"m1\"},{\"id\":\"m2\"}]}");
        var lister = new HttpProviderModelLister(new FakeStore([Provider()], []), new FakeSecrets("sekret"), new HttpClient(handler));

        var ids = await lister.ListModelIdsAsync("p", TestContext.Current.CancellationToken);

        Assert.Equal(["m1", "m2"], ids);
        Assert.Equal("https://example.test/v1/models", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer sekret", handler.Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Lister_answers_null_for_other_adapters_missing_secrets_errors_and_bad_bodies()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeStore([Provider("p"), Provider("g", adapter: "unknown_family")], []);

        Assert.Null(await new HttpProviderModelLister(store, new FakeSecrets("k"), new HttpClient(new FakeHandler(HttpStatusCode.OK, "{\"data\":[]}"))).ListModelIdsAsync("g", ct));
        Assert.Null(await new HttpProviderModelLister(store, new FakeSecrets(null), new HttpClient(new FakeHandler(HttpStatusCode.OK, "{\"data\":[]}"))).ListModelIdsAsync("p", ct));
        Assert.Null(await new HttpProviderModelLister(store, new FakeSecrets("k"), new HttpClient(new FakeHandler(HttpStatusCode.Unauthorized, ""))).ListModelIdsAsync("p", ct));
        Assert.Null(await new HttpProviderModelLister(store, new FakeSecrets("k"), new HttpClient(new FakeHandler(HttpStatusCode.OK, "nope"))).ListModelIdsAsync("p", ct));
        Assert.Null(await new HttpProviderModelLister(store, new FakeSecrets("k"), new HttpClient(new ThrowingHandler())).ListModelIdsAsync("p", ct));
        Assert.Null(await new HttpProviderModelLister(store, new FakeSecrets("k"), new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}"))).ListModelIdsAsync("unknown", ct));
    }

    private sealed class FakeStore : ICatalogueStore
    {
        private readonly List<GwProvider> _providers;

        public FakeStore(IEnumerable<GwProvider> providers, IEnumerable<GwModel> models)
        {
            _providers = [.. providers];
            Models = models.ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        }

        public Dictionary<string, GwModel> Models { get; }

        public int Writes { get; private set; }

        public Task<IReadOnlyList<GwProvider>> ListProvidersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GwProvider>>(_providers);

        public Task<IReadOnlyList<GwModel>> ListModelsAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GwModel>>([.. Models.Values.Where(m => m.ProviderId == providerId)]);

        public Task UpsertModelAsync(GwModel model, CancellationToken cancellationToken)
        {
            Writes++;
            Models[model.ModelId] = model;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLister(IReadOnlyList<string>? ids) : IProviderModelLister
    {
        public Task<IReadOnlyList<string>?> ListModelIdsAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult(ids);
    }

    private sealed class FakeSecrets(string? value) : IProviderSecretSource
    {
        public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct) => ValueTask.FromResult(value);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("down");
    }
}
