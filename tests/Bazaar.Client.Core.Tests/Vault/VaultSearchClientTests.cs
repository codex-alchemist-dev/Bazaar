using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Server;
using Bazaar.Client.Core.Vault;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Vault;

public class VaultSearchClientTests
{
    private static readonly Guid _vault = Guid.Parse("00000000-0000-7000-8000-00000000d001");
    private static readonly Guid _doc = Guid.Parse("00000000-0000-7000-8000-00000000e001");

    private const string _searchJson = """
        {"cards":[{"doc_id":"00000000-0000-7000-8000-00000000e001","ordinal":2,"path":"notes/auth.md","title":"Auth","type":"note",
        "project":"bazaar","heading_path":["Auth","Login"],"basis":"Extracted","score":0.5,"gist":"Login flow","approx_tokens":3}],
        "lexical_only":true,"indexed_commit":"abc123"}
        """;

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (VaultApi Api, FakeHandler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> respond, string? token = "secret")
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080/") };
        return (new VaultApi(http, () => token), handler);
    }

    [Fact]
    public async Task Search_sends_the_bearer_token_and_parses_snake_case_cards()
    {
        var (api, handler) = Client(_ => Json(_searchJson));

        var response = await api.SearchAsync(_vault, "auth flow", "bazaar", null, 5, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("secret", request.Headers.Authorization.Parameter);
        Assert.Equal($"/vault/search", request.RequestUri!.AbsolutePath);
        Assert.Contains("q=auth%20flow", request.RequestUri.Query);
        Assert.Contains("project=bazaar", request.RequestUri.Query);
        Assert.Contains("limit=5", request.RequestUri.Query);
        Assert.DoesNotContain("type=", request.RequestUri.Query);

        Assert.True(response.LexicalOnly);
        Assert.Equal("abc123", response.IndexedCommit);
        var card = Assert.Single(response.Cards);
        Assert.Equal(_doc, card.DocId);
        Assert.Equal(2, card.Ordinal);
        Assert.Equal(["Auth", "Login"], card.HeadingPath);
        Assert.Equal("Extracted", card.Basis);
    }

    [Fact]
    public async Task Search_without_a_credential_sends_no_authorization_header()
    {
        var (api, handler) = Client(_ => Json(_searchJson), token: null);

        await api.SearchAsync(_vault, "auth", null, null, 5, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task Fetch_body_returns_the_block_text()
    {
        var (api, handler) = Client(_ => Json("""{"body":"# Auth\nThe login flow."}"""));

        var body = await api.FetchBodyAsync(_vault, _doc, 2, TestContext.Current.CancellationToken);

        Assert.Equal("# Auth\nThe login flow.", body);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/vault/doc", request.RequestUri!.AbsolutePath);
        Assert.Contains($"doc={_doc:D}", request.RequestUri.Query);
        Assert.Contains("ordinal=2", request.RequestUri.Query);
    }

    [Fact]
    public async Task A_refused_request_surfaces_its_status()
    {
        var (api, _) = Client(_ => Json("""{"error":"unavailable"}""", HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<VaultApiException>(() =>
            api.SearchAsync(_vault, "auth", null, null, 5, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.Status);
    }

    [Fact]
    public async Task View_model_searches_then_opens_a_card()
    {
        var (api, _) = Client(request => request.RequestUri!.AbsolutePath == "/vault/doc"
            ? Json("""{"body":"The login flow."}""")
            : Json(_searchJson));
        var pane = new VaultSearchPaneViewModel(Descriptor(), api) { Query = "auth" };

        await pane.SearchAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1 match (text match only; no embedding profile).", pane.StatusMessage);
        var card = Assert.Single(pane.Results);
        await pane.OpenAsync(card, TestContext.Current.CancellationToken);
        Assert.Same(card, pane.OpenedCard);
        Assert.Equal("The login flow.", pane.OpenedBody);
    }

    [Fact]
    public async Task A_document_the_caller_cannot_read_says_so()
    {
        var (api, _) = Client(_ => Json("""{"error":"not_found"}""", HttpStatusCode.NotFound));
        var pane = new VaultSearchPaneViewModel(Descriptor(), api);
        var card = new VaultCardDto(_doc, 0, "p.md", "P", "note", null, [], "Inferred", 0.1, "gist", 1);

        await pane.OpenAsync(card, TestContext.Current.CancellationToken);

        Assert.Equal("This document is not available to you.", pane.OpenedBody);
    }

    [Fact]
    public void Registry_without_an_explicit_api_builds_one_for_the_configured_endpoint()
    {
        var registry = FirstPartyPanes.CreateRegistry();

        Assert.True(registry.Contains(PaneKinds.VaultSearch));
        Assert.IsType<VaultSearchPaneViewModel>(registry.Create(Descriptor()));
    }

    [Fact]
    public async Task Factory_sends_the_endpoint_base_address_and_credential()
    {
        var handler = new FakeHandler(_ => Json(_searchJson));
        var api = VaultApiFactory.Create(new ServerEndpoint(new Uri("http://example.test:9000/base/"), "tok-1"), handler);

        await api.SearchAsync(_vault, "auth", null, null, 3, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("http://example.test:9000/base/vault/search", request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal("tok-1", request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public void Client_server_starts_at_the_development_default_and_takes_a_new_endpoint()
    {
        var original = ClientServer.Current;
        try
        {
            Assert.Equal(ServerEndpoint.Development, ClientServer.Current);
            var paired = new ServerEndpoint(new Uri("http://10.0.0.5:8080/"), "paired");
            ClientServer.Use(paired);
            Assert.Same(paired, ClientServer.Current);
        }
        finally
        {
            ClientServer.Use(original);
        }
    }

    [Fact]
    public void Registry_with_a_vault_api_builds_the_search_pane_and_rejects_a_bad_vault_field()
    {
        var (api, _) = Client(_ => Json(_searchJson));
        var registry = FirstPartyPanes.CreateRegistry(vault: api);

        Assert.IsType<VaultSearchPaneViewModel>(registry.Create(Descriptor()));

        var bad = Descriptor(vault: "not-a-guid");
        Assert.IsType<GenericPaneViewModel>(registry.Create(bad));
    }

    [AvaloniaFact]
    public async Task Search_view_draws_results_and_opens_one_into_the_body_text()
    {
        var (api, _) = Client(request => request.RequestUri!.AbsolutePath == "/vault/doc"
            ? Json("""{"body":"The login flow."}""")
            : Json(_searchJson));
        var pane = new VaultSearchPaneViewModel(Descriptor(), api) { Query = "auth" };
        var view = new VaultSearchView(pane);
        var window = new Window { Content = view };
        window.Show();

        await pane.SearchAsync(TestContext.Current.CancellationToken);
        using var _ = window.CaptureRenderedFrame(); // forces layout so the result buttons materialise

        var status = Named<TextBlock>(view, "StatusText");
        Assert.NotNull(status);
        Assert.True(status.IsVisible);
        Assert.Contains("1 match", status.Text);

        var results = Named<StackPanel>(view, "ResultsPanel");
        Assert.NotNull(results);
        var button = Assert.Single(results.GetLogicalDescendants().OfType<Button>());
        var titles = button.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Auth", titles);

        var body = Named<TextBlock>(view, "OpenedBody");
        Assert.NotNull(body);
        Assert.False(body.IsVisible);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 50 && body.Text is null; i++) await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(body.IsVisible);
        Assert.Equal("The login flow.", body.Text);
    }

    private static T? Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);

    private static PaneDescriptor Descriptor(string vault = "00000000-0000-7000-8000-00000000d001") =>
        new(PaneKinds.VaultSearch, "Vault search", "vault.read",
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["vault"] = vault }));
}
