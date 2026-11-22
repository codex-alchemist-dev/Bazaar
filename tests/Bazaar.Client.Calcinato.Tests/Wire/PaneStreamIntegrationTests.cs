using Bazaar.Client.Calcinato.Sources;
using Bazaar.Client.Calcinato.Tests.Sources;
using Bazaar.Client.Calcinato.Wire;
using Bazaar.Contracts.Terminal;

namespace Bazaar.Client.Calcinato.Tests.Wire;

public sealed class PaneStreamIntegrationTests
{
    [Fact]
    public async Task RemoteTerminalSource_over_the_hub_opens_outputs_recovers_a_gap_by_replay_and_ends_on_exit()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new FakePaneConnection();
        var server = new FakePaneServer(connection, "t1");
        await using var hub = new PaneStreamTerminalWireHub(connection);
        await using var source = new RemoteTerminalSource(hub.ForTerminal("t1"), "t1");

        // Open: the server answers TerminalOpened, which the source waits for.
        await source.OpenAsync(80, 24, ct);
        Assert.True(source.CanWrite);

        // Output in order, then a live gap: sequence 3 is kept by the server but never sent live.
        server.Output(1, 0x41);
        server.Output(2, 0x42);
        server.Output(3, 0x43, live: false);
        server.Output(4, 0x44);

        var first = await Eventually.NextAsync(source.Output);
        var second = await Eventually.NextAsync(source.Output);
        var third = await Eventually.NextAsync(source.Output);
        var fourth = await Eventually.NextAsync(source.Output);

        Assert.Equal((1L, false), (first.Seq, first.IsReplay));
        Assert.Equal((2L, false), (second.Seq, second.IsReplay));
        Assert.Equal((3L, true), (third.Seq, third.IsReplay));
        Assert.Equal((4L, true), (fourth.Seq, fourth.IsReplay));
        Assert.Equal([0x43], third.Bytes.ToArray());

        var replays = server.Received.OfType<TerminalReplay>().ToArray();
        Assert.Equal(3, Assert.Single(replays).FromSeq);

        // Exit ends the output and sets the exit code.
        server.Exit(0);
        Assert.Equal(0, await source.Exited.WaitAsync(ct));
        Assert.False(await source.Output.WaitToReadAsync(ct));
        Assert.Equal(["pane:t1"], connection.Subscribes);
    }
}
