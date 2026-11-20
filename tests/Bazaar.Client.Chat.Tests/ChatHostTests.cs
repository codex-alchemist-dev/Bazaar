using Avalonia.Headless.XUnit;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Contracts.Wire;
using Bazaar.Testing;

namespace Bazaar.Client.Chat.Tests;

public class ChatHostTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");

    private static GcChatSession Session(FakeWireTransport transport)
    {
        var client = new WireClient(transport, transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "linux-test", DeviceClass = "linux", ClientBuild = "test", Jitter = () => 1.0 },
            new FakeClock());
        var settings = new ChatConnectionSettings(new Uri("ws://localhost/ws"), "token", _gc, "me");
        return new GcChatSession(client, settings, new ChatTimeline(new Dictionary<Guid, ChatMember>(), "me", TimeZoneInfo.Utc));
    }

    [AvaloniaFact]
    public async Task Start_builds_a_view_bound_to_the_model_and_starts_the_session()
    {
        var transport = new FakeWireTransport();
        await using var host = ChatHost.Start(Session(transport), null, _ => { });

        Assert.Same(host.Model, host.View.DataContext);
        await host.Started.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Until(() => transport.Count == 1 && transport.Latest.SentOfType(WireTypes.Subscribe).Any());
    }

    [Fact]
    public async Task A_failed_start_is_logged_once_and_never_faults_the_task()
    {
        var lines = new List<string>();

        await ChatHost.StartLoggingAsync(_ => throw new InvalidOperationException("server unreachable"), lines.Add, CancellationToken.None);

        Assert.Equal(["Bazaar: chat failed to start: server unreachable"], lines);
    }

    [Fact]
    public async Task A_cancelled_start_is_not_logged()
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await ChatHost.StartLoggingAsync(token => Task.Delay(Timeout.Infinite, token), lines.Add, cts.Token);

        Assert.Empty(lines);
    }

    [AvaloniaFact]
    public async Task Dispose_is_idempotent()
    {
        var host = ChatHost.Start(Session(new FakeWireTransport()), null, _ => { });

        await host.DisposeAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public void Start_rejects_a_missing_session_and_a_missing_log()
    {
        Assert.Throws<ArgumentNullException>(() => ChatHost.Start((GcChatSession)null!, null, _ => { }));
        Assert.Throws<ArgumentNullException>(() => ChatHost.Start(Session(new FakeWireTransport()), null, null!));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
