using System.Collections.Concurrent;
using System.Text;
using Bazaar.Client.Calcinato.Sources;
using Bazaar.Client.Calcinato.Tests.Sources;
using Bazaar.Client.Calcinato.Wire;
using Bazaar.Contracts.Terminal;

namespace Bazaar.Client.Calcinato.Tests.Wire;

public sealed class PaneStreamTerminalWireHubTests
{
    private static readonly byte[] _outputA = [0x41];
    private static readonly byte[] _outputB = [0x42];

    private static TerminalOpen Open(string id) => new(id, null, 80, 24);

    [Fact]
    public async Task Frames_are_routed_only_to_the_terminal_they_belong_to()
    {
        var connection = new FakePaneConnection();
        await using var hub = new PaneStreamTerminalWireHub(connection);
        var t1 = hub.ForTerminal("t1");
        var t2 = hub.ForTerminal("t2");

        connection.Push("pane:t1", new TerminalOutput("t1", 1, _outputA));
        connection.Push("pane:t2", new TerminalOutput("t2", 1, _outputB));
        connection.Push("pane:t1", new TerminalExit("t1", 0));

        var first = Assert.IsType<TerminalOutput>(await Eventually.NextAsync(t2.Incoming));
        Assert.Equal(_outputB, first.Bytes);
        var a = Assert.IsType<TerminalOutput>(await Eventually.NextAsync(t1.Incoming));
        Assert.Equal(_outputA, a.Bytes);
        Assert.IsType<TerminalExit>(await Eventually.NextAsync(t1.Incoming));
        Assert.False(t1.Incoming.TryRead(out _));
    }

    [Fact]
    public async Task ForTerminal_returns_the_same_wire_while_it_lives()
    {
        await using var hub = new PaneStreamTerminalWireHub(new FakePaneConnection());

        Assert.Same(hub.ForTerminal("t1"), hub.ForTerminal("t1"));
        Assert.NotSame(hub.ForTerminal("t1"), hub.ForTerminal("t2"));
    }

    [Fact]
    public async Task ForTerminal_refuses_an_invalid_terminal_id()
    {
        await using var hub = new PaneStreamTerminalWireHub(new FakePaneConnection());
        Assert.Throws<ArgumentException>(() => hub.ForTerminal("bad id!"));
        Assert.Throws<ArgumentException>(() => hub.ForTerminal(string.Empty));
    }

    [Fact]
    public async Task Subscribe_happens_lazily_once_and_release_unsubscribes()
    {
        var connection = new FakePaneConnection();
        await using var hub = new PaneStreamTerminalWireHub(connection);
        var wire = hub.ForTerminal("t1");
        _ = wire.Incoming;
        Assert.Empty(connection.Subscribes);

        var ct = TestContext.Current.CancellationToken;
        await wire.SendAsync(Open("t1"), ct);
        await wire.SendAsync(new TerminalInput("t1", _outputA), ct);
        Assert.Equal(["pane:t1"], connection.Subscribes);
        Assert.Equal(2, connection.SentTo("pane:t1").Count);

        await hub.ReleaseAsync("t1");

        Assert.Equal(["pane:t1"], connection.Unsubscribes);
        Assert.False(await wire.Incoming.WaitToReadAsync(ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await wire.SendAsync(new TerminalInput("t1", _outputA), ct));
        Assert.NotSame(wire, hub.ForTerminal("t1"));
    }

    [Fact]
    public async Task Undecodable_and_misrouted_bodies_are_dropped_and_counted_without_throwing()
    {
        var connection = new FakePaneConnection();
        await using var hub = new PaneStreamTerminalWireHub(connection);
        var wire = hub.ForTerminal("t1");

        connection.Push("pane:t1", [0x7b, 0x7b]);
        connection.Push("pane:t1", Encoding.UTF8.GetBytes("{\"type\":\"nope\",\"terminalId\":\"t1\"}"));
        connection.Push("pane:t1", Encoding.UTF8.GetBytes("{\"type\":\"output\",\"terminalId\":\"t2\",\"seq\":1,\"bytes\":\"QQ==\"}"));
        connection.Push("gc:not-a-pane", Encoding.UTF8.GetBytes("{\"type\":\"exit\",\"terminalId\":\"t1\",\"code\":0}"));
        connection.Push("pane:t1", new TerminalOutput("t1", 1, _outputA));

        var frame = Assert.IsType<TerminalOutput>(await Eventually.NextAsync(wire.Incoming));
        Assert.Equal(1, frame.Seq);
        Assert.Equal(3, hub.DroppedFrames);
    }

    [Fact]
    public async Task Reconnect_resubscribes_every_active_terminal_and_gives_fresh_incoming()
    {
        var connection = new FakePaneConnection();
        await using var hub = new PaneStreamTerminalWireHub(connection);
        var ct = TestContext.Current.CancellationToken;
        var t1 = hub.ForTerminal("t1");
        hub.ForTerminal("idle");
        await t1.SendAsync(Open("t1"), ct);
        Assert.Equal(["pane:t1"], connection.Subscribes);

        connection.Drop();
        Assert.False(await t1.Incoming.WaitToReadAsync(ct));

        connection.Reconnect();
        await hub.OnReconnectedAsync(ct);

        Assert.Equal(["pane:t1", "pane:t1"], connection.Subscribes);
        connection.Push("pane:t1", new TerminalOpened("t1", WriterHeld: false, ReplayFrom: 5));
        var opened = Assert.IsType<TerminalOpened>(await Eventually.NextAsync(t1.Incoming));
        Assert.Equal(5, opened.ReplayFrom);
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_frame()
    {
        var connection = new FakePaneConnection();
        await using var hub = new PaneStreamTerminalWireHub(connection, capacity: 2);
        var t1 = hub.ForTerminal("t1");
        var t2 = hub.ForTerminal("t2");
        var ct = TestContext.Current.CancellationToken;
        await t1.SendAsync(Open("t1"), ct);

        for (long seq = 1; seq <= 4; seq++)
        {
            connection.Push("pane:t1", new TerminalOutput("t1", seq, [(byte)seq]));
        }

        // The sentinel on t2 is routed after the four t1 frames, so the pump has finished them when it is read.
        connection.Push("pane:t2", new TerminalOutput("t2", 1, _outputB));
        await Eventually.NextAsync(t2.Incoming);

        Assert.Equal(3, (await Eventually.NextAsync(t1.Incoming) as TerminalOutput)!.Seq);
        Assert.Equal(4, (await Eventually.NextAsync(t1.Incoming) as TerminalOutput)!.Seq);
        Assert.False(t1.Incoming.TryRead(out _));
    }

    [Fact]
    public async Task Concurrent_use_keeps_one_wire_per_id_and_routes_every_frame()
    {
        var connection = new FakePaneConnection();
        await using var hub = new PaneStreamTerminalWireHub(connection, capacity: 1024);
        var ct = TestContext.Current.CancellationToken;
        const int terminals = 10;
        const int frames = 50;

        var seen = new ConcurrentDictionary<string, ConcurrentBag<ITerminalWire>>();
        await Parallel.ForAsync(0, terminals * 20, ct, (i, _) =>
        {
            var id = "t" + (i % terminals);
            seen.GetOrAdd(id, _ => []).Add(hub.ForTerminal(id));
            return ValueTask.CompletedTask;
        });
        Assert.All(seen.Values, bag => Assert.Single(bag.Distinct()));

        var wires = Enumerable.Range(0, terminals).Select(i => hub.ForTerminal("t" + i)).ToArray();
        await Task.WhenAll(wires.Select((w, i) => Task.Run(async () =>
        {
            for (var n = 0; n < frames; n++)
            {
                await w.SendAsync(new TerminalInput("t" + i, []), ct);
            }
        }, ct)));

        Assert.Equal(terminals, connection.Subscribes.Distinct().Count());
        Assert.Equal(terminals, connection.Subscribes.Count);
        Assert.Equal(terminals * frames, connection.Sent.Count);

        for (var i = 0; i < terminals; i++)
        {
            connection.Push("pane:t" + i, new TerminalOutput("t" + i, 1, [(byte)i]));
        }

        for (var i = 0; i < terminals; i++)
        {
            var frame = Assert.IsType<TerminalOutput>(await Eventually.NextAsync(wires[i].Incoming));
            Assert.Equal((byte)i, frame.Bytes[0]);
        }
    }

    [Fact]
    public async Task Disposing_the_hub_unsubscribes_completes_every_wire_and_refuses_new_use()
    {
        var connection = new FakePaneConnection();
        var hub = new PaneStreamTerminalWireHub(connection);
        var ct = TestContext.Current.CancellationToken;
        var t1 = hub.ForTerminal("t1");
        var t2 = hub.ForTerminal("t2");
        await t1.SendAsync(Open("t1"), ct);
        await t2.SendAsync(Open("t2"), ct);

        await hub.DisposeAsync();
        await hub.DisposeAsync();

        Assert.Equal(["pane:t1", "pane:t2"], connection.Unsubscribes.OrderBy(s => s).ToArray());
        Assert.False(await t1.Incoming.WaitToReadAsync(ct));
        Assert.False(await t2.Incoming.WaitToReadAsync(ct));
        Assert.Throws<ObjectDisposedException>(() => hub.ForTerminal("t3"));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await hub.OnReconnectedAsync(ct));
    }

    [Fact]
    public async Task Sending_a_frame_for_another_terminal_is_refused()
    {
        await using var hub = new PaneStreamTerminalWireHub(new FakePaneConnection());
        var wire = hub.ForTerminal("t1");

        await Assert.ThrowsAsync<ArgumentException>(async () => await wire.SendAsync(Open("t2"), TestContext.Current.CancellationToken));
    }
}
