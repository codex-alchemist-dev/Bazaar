using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.ColdArchive;
using Bazaar.Client.Core;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Linux.Tests;

/// <summary>The Linux head composes the cold-storage pane and draws it for the <c>--cold-archive</c> flag.</summary>
public class ColdArchivePaneTests
{
    private sealed class NoServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private static HttpColdArchiveClient Client() =>
        HttpColdArchiveClient.Create(new ServerEndpoint(new Uri("https://bazaar.invalid/"), "pin"), new NoServer());

    [AvaloniaFact]
    public void The_registry_holds_the_cold_storage_pane_kind()
    {
        var registry = LinuxColdArchivePanes.Compose(Client());

        Assert.Contains(PaneKinds.ColdArchive, registry.Kinds);
    }

    [AvaloniaFact]
    public void Show_replaces_the_window_content_with_the_pane()
    {
        var window = new MainWindow();

        LinuxColdArchivePanes.Show(window, Client());

        var scroll = Assert.IsType<ScrollViewer>(window.Content);
        var stack = Assert.IsType<StackPanel>(scroll.Content);
        Assert.Single(stack.Children);
    }
}
