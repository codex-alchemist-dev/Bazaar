using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.ColdArchive.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.ColdArchive.Tests;

public sealed class ColdArchiveTests
{
    private static PaneDescriptor Descriptor() => new(PaneKinds.ColdArchive, "Archive", string.Empty, JsonElement.Parse("{}"));

    private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static HttpColdArchiveClient Client(Handler handler) =>
        HttpColdArchiveClient.Create(new ServerEndpoint(new Uri("http://127.0.0.1:8080/"), "tok"), handler);

    [Fact]
    public async Task The_http_client_calls_each_route_with_the_bearer_and_reads_the_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/archive/units" => (HttpStatusCode.OK, """{"units":[{"kind":"gc","id":"gc-1"}]}"""),
            "/archive/units/gc/gc-1" => (HttpStatusCode.OK, """{"kind":"gc","id":"gc-1","records":3,"segments":1,"rootHash":"abc"}"""),
            "/archive/units/gc/gc-1/verify" => (HttpStatusCode.OK, """{"ok":false,"problems":["x"],"rootHash":null}"""),
            "/archive/units/gc/gc-1/restore" => (HttpStatusCode.OK, """{"restored":0,"alreadyPresent":2,"conflicts":["seq-2"]}"""),
            _ => (HttpStatusCode.OK, """{"hits":[{"kind":"gc","unit":"gc-1","recordId":"seq-1","at":"2026-11-01T00:00:00+00:00","snippet":"needle"}]}"""),
        });
        var client = Client(handler);

        Assert.Equal("gc-1", (await client.ListAsync(ct))[0].Id);
        Assert.Equal(3, (await client.ArchiveAsync("gc", "gc-1", ct)).Records);
        var verify = await client.VerifyAsync("gc", "gc-1", ct);
        Assert.False(verify.Ok);
        Assert.Equal(["x"], verify.Problems);
        Assert.Equal(["seq-2"], (await client.RestoreAsync("gc", "gc-1", ct)).Conflicts);
        Assert.Equal("needle", (await client.SearchAsync("a b", ct))[0].Snippet);
        Assert.All(handler.Seen, s => Assert.Equal("Bearer tok", s.Auth));
        Assert.Contains(handler.Seen, s => s.Path == "/archive/search?q=a%20b");
    }

    [Fact]
    public async Task A_refusal_throws_its_code()
    {
        var client = Client(new Handler(_ => (HttpStatusCode.Conflict, """{"error":"already_archived"}""")));
        var ex = await Assert.ThrowsAsync<ArchiveRequestException>(() => client.ArchiveAsync("gc", "gc-1", TestContext.Current.CancellationToken));
        Assert.Equal((409, "already_archived"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task The_pane_lists_archives_verifies_and_reports_restore()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/archive/units" => (HttpStatusCode.OK, """{"units":[]}"""),
            "/archive/units/gc/gc-1" => (HttpStatusCode.OK, """{"kind":"gc","id":"gc-1","records":3,"segments":1,"rootHash":"abcdef0123456789"}"""),
            "/archive/units/gc/gc-1/verify" => (HttpStatusCode.OK, """{"ok":true,"problems":[],"rootHash":"abcdef0123456789"}"""),
            _ => (HttpStatusCode.OK, """{"restored":0,"alreadyPresent":2,"conflicts":["seq-2"]}"""),
        });
        var pane = new ColdArchivePaneViewModel(Descriptor(), Client(handler));

        await pane.RefreshAsync(ct);
        Assert.Equal("Nothing is archived yet.", pane.Status);

        await pane.ArchiveAsync(ct);
        Assert.Equal("Pick or type a unit id first.", pane.Status);

        pane.UnitId = "gc-1";
        await pane.ArchiveAsync(ct);
        Assert.Contains("Archived 3 records", pane.Status);
        Assert.Single(pane.Units);

        await pane.VerifyAsync(ct);
        Assert.StartsWith("Verified.", pane.Status);

        await pane.RestoreAsync(ct);
        Assert.Contains("1 conflicts", pane.Status);
        Assert.Contains("seq-2", pane.Status);
    }

    [Fact]
    public async Task The_pane_explains_a_refusal_and_a_missing_client_and_searches()
    {
        var ct = TestContext.Current.CancellationToken;
        var denied = new ColdArchivePaneViewModel(Descriptor(), Client(new Handler(_ => (HttpStatusCode.Forbidden, """{"error":"forbidden"}"""))));
        await denied.RefreshAsync(ct);
        Assert.Equal("Only the owner can use the archive.", denied.Status);

        var none = new ColdArchivePaneViewModel(Descriptor(), null);
        Assert.Equal("No archive connection.", none.Status);
        await none.RefreshAsync(ct);
        Assert.Equal("No archive connection.", none.Status);

        var found = new ColdArchivePaneViewModel(Descriptor(), Client(new Handler(_ =>
            (HttpStatusCode.OK, """{"hits":[{"kind":"gc","unit":"gc-1","recordId":"seq-1","at":"2026-11-01T00:00:00+00:00","snippet":"needle"}]}"""))));
        await found.SearchAsync(ct);
        Assert.Equal("Type something to search.", found.Status);
        found.Query = "needle";
        await found.SearchAsync(ct);
        Assert.Single(found.Hits);
        Assert.Equal("1 matches.", found.Status);
    }

    [AvaloniaFact]
    public void The_pane_registers_and_renders_headless()
    {
        var registry = PaneRegistry.Compose(PaneViews.Instance, ColdArchivePanes.Modules, new PaneModuleContext().With<IColdArchiveClient>(Client(new Handler(_ => (HttpStatusCode.OK, "{}")))));
        Assert.True(registry.Contains(PaneKinds.ColdArchive));

        var pane = new ColdArchivePaneViewModel(Descriptor(), null);
        var view = PaneRegistry.Compose(PaneViews.Instance, ColdArchivePanes.Modules, new PaneModuleContext()).CreateView(pane);
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("No archive connection.", texts);
        Assert.Contains(view.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Restore report"));
        window.Close();
    }
}
