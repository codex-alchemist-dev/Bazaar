using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Core.Vault;
using Bazaar.Client.GC;
using Bazaar.Contracts.Panes;

namespace Bazaar.Accessibility.Avalonia.Tests;

/// <summary>
/// Loads the head-shared windows whose controls were named (doc 41 section 4.7: every interactive control has a spoken
/// name) and asserts that the name scan finds nothing. No live services are contacted: the Vault search view is fed a
/// canned search response.
/// </summary>
public class HeadWindowNameTests
{
    private static readonly Guid _vault = Guid.Parse("00000000-0000-7000-8000-00000000d001");

    private const string _searchJson = """
        {"cards":[{"doc_id":"00000000-0000-7000-8000-00000000e001","ordinal":2,"path":"notes/auth.md","title":"Auth","type":"note",
        "project":"bazaar","heading_path":["Auth","Login"],"basis":"Extracted","score":0.5,"gist":"Login flow","approx_tokens":3}],
        "lexical_only":true,"indexed_commit":"abc123"}
        """;

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    [AvaloniaFact]
    public void Chat_view_send_button_and_draft_box_have_names()
    {
        var view = new ChatView();
        var window = new Window { Content = view };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
        Assert.Equal("Send message", AutomationProperties.GetName(window.GetLogicalDescendants().OfType<Button>().Single()));
        Assert.Equal("Message draft", AutomationProperties.GetName(window.GetLogicalDescendants().OfType<TextBox>().Single()));
    }

    [AvaloniaFact]
    public async Task Vault_search_view_query_button_and_result_cards_have_names()
    {
        var http = new HttpClient(new CannedHandler(_searchJson)) { BaseAddress = new Uri("http://localhost:8080/") };
        var api = new VaultApi(http, () => "secret");
        var descriptor = new PaneDescriptor(PaneKinds.VaultSearch, "Vault search", "vault.read",
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["vault"] = _vault.ToString() }));
        var pane = new VaultSearchPaneViewModel(descriptor, api) { Query = "auth" };
        await pane.SearchAsync(TestContext.Current.CancellationToken);

        var window = new Window { Content = new VaultSearchView(pane) };
        window.Show();

        Assert.Empty(AccessibleControlScan.Find(window));
        Assert.Equal("Search the Vault", AutomationProperties.GetName(window.GetLogicalDescendants().OfType<TextBox>().Single()));
        Assert.Equal("Run Vault search", AutomationProperties.GetName(window.GetLogicalDescendants().OfType<Button>().First()));
        Assert.Equal("Open Auth", AutomationProperties.GetName(window.GetLogicalDescendants().OfType<Button>().Last()));
    }
}
