using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat;

namespace Bazaar.Client.ServerE2E.Tests;

/// <summary>
/// The Avalonia.Headless chat client against a running <c>Bazaar.Server.Host</c> (doc 34, layers L3 and L4 together): sign in with
/// the dev token, open the group chat, send a message, and see the agent's reply, which came through the host's gateway.
/// </summary>
public sealed class ClientAgainstRealServerTests(RealServerFixture server) : IClassFixture<RealServerFixture>
{
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(60);

    [AvaloniaFact]
    public async Task A_message_sent_from_the_headless_client_gets_the_agents_reply_from_the_real_server()
    {
        Assert.SkipWhen(PgUnavailable, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

        var settings = new ChatConnectionSettings(server.SocketUri, server.Token, server.GcId, "Owner");
        var log = new List<string>();
        await using var host = ChatHost.Start(settings, new InMemoryCursorStore(), "linux", "e2e", a => Dispatcher.UIThread.Post(a), log.Add);
        var window = new Window { Content = host.View, Width = 800, Height = 600 };
        window.Show();

        await host.Started.WaitAsync(_patience);
        Assert.Empty(log);

        var draft = window.GetVisualDescendants().OfType<TextBox>().Single();
        draft.Text = "Hello agent";
        await host.Model.SendDraftAsync();

        await WaitForAsync(
            () => host.Model.Lines.Any(line => line.Text == FakeModelUpstream.Reply),
            () => $"No reply reached the client. Lines: [{string.Join(" | ", host.Model.Lines.Select(l => l.Author + ": " + l.Text))}]\nHost output:\n{server.HostOutput}");

        var lines = host.Model.Lines.ToList();
        Assert.Contains(lines, line => line.Own && line.Text == "Hello agent");
        var reply = Assert.Single(lines, line => line.Text == FakeModelUpstream.Reply);
        Assert.Equal(server.AgentId, reply.AuthorId);
        Assert.Equal(1, server.Upstream.Calls);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(2, list.ItemCount);
    }

    private static bool PgUnavailable => Bazaar.Testing.Postgres.PgSource.IsUnsupportedHost;

    private static async Task WaitForAsync(Func<bool> condition, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + _patience;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(failure());
    }
}
