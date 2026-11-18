using System.Collections.Concurrent;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Sources;
using Bazaar.Contracts.Terminal;

namespace Bazaar.Client.Calcinato.Tests.Sources;

public sealed class RemoteTerminalSourceTests
{
    private const string Id = "t1";

    private static async Task<RemoteTerminalSource> OpenAsync(FakeWire wire, bool writerHeld = false, long replayFrom = 1, int capacity = 256)
    {
        var source = new RemoteTerminalSource(wire, Id, capacity);
        var open = source.OpenAsync(80, 24, TestContext.Current.CancellationToken);
        wire.Push(new TerminalOpened(Id, writerHeld, replayFrom));
        await open;
        return source;
    }

    private static TerminalOutput Out(long seq, byte value) => new(Id, seq, [value]);

    [Fact]
    public async Task Open_sends_TerminalOpen_and_a_held_writer_makes_the_viewer_read_only()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire, writerHeld: true);

        var open = Assert.IsType<TerminalOpen>(Assert.Single(wire.Sent));
        Assert.Null(open.AgentId);
        Assert.Equal((80, 24), (open.Cols, open.Rows));
        Assert.True(source.IsRemote);
        Assert.False(source.CanWrite);
    }

    [Fact]
    public async Task Writer_role_is_granted_when_nobody_holds_it()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire, writerHeld: false);

        Assert.True(source.CanWrite);
    }

    [Fact]
    public async Task In_order_output_is_delivered_without_replay_flag()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire);

        wire.Push(Out(1, 0x41));
        wire.Push(Out(2, 0x42));

        var first = await Eventually.NextAsync(source.Output);
        var second = await Eventually.NextAsync(source.Output);
        Assert.Equal((1L, false), (first.Seq, first.IsReplay));
        Assert.Equal((2L, false), (second.Seq, second.IsReplay));
        Assert.Empty(wire.SentOf<TerminalReplay>());
    }

    [Fact]
    public async Task Duplicate_and_already_seen_sequences_are_dropped()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire);

        wire.Push(Out(1, 0x41));
        wire.Push(Out(1, 0x41));
        wire.Push(Out(2, 0x42));
        wire.Push(Out(1, 0x41));
        wire.Push(Out(3, 0x43));

        var seqs = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            seqs.Add((await Eventually.NextAsync(source.Output)).Seq);
        }

        Assert.Equal([1L, 2L, 3L], seqs);
        Assert.Empty(wire.SentOf<TerminalReplay>());
    }

    [Fact]
    public async Task Gap_requests_one_replay_from_the_first_missing_sequence_and_flags_replayed_chunks()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire);

        wire.Push(Out(1, 1));
        wire.Push(Out(3, 3));
        wire.Push(Out(4, 4));
        wire.Push(Out(2, 2));
        wire.Push(Out(3, 3));
        wire.Push(Out(4, 4));

        var chunks = new List<TerminalChunk>();
        for (var i = 0; i < 4; i++)
        {
            chunks.Add(await Eventually.NextAsync(source.Output));
        }

        Assert.Equal([1L, 2L, 3L, 4L], chunks.Select(c => c.Seq));
        Assert.Equal([false, true, true, false], chunks.Select(c => c.IsReplay));
        var replay = Assert.Single(wire.SentOf<TerminalReplay>());
        Assert.Equal(2, replay.FromSeq);
    }

    [Fact]
    public async Task Out_of_order_frame_is_recovered_by_replay_without_duplicates()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire);

        wire.Push(Out(1, 1));
        wire.Push(Out(3, 3));
        wire.Push(Out(2, 2));
        wire.Push(Out(3, 3));

        var seqs = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            seqs.Add((await Eventually.NextAsync(source.Output)).Seq);
        }

        Assert.Equal([1L, 2L, 3L], seqs);
        Assert.Single(wire.SentOf<TerminalReplay>());
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_chunk_and_requests_the_lost_range_on_the_next_frame()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire, capacity: 2);
        var statuses = new ConcurrentQueue<TerminalSourceStatus>();
        source.StatusChanged += (_, s) => statuses.Enqueue(s);

        wire.Push(Out(1, 1));
        wire.Push(Out(2, 2));
        wire.Push(Out(3, 3));
        await Eventually.TrueAsync(() => statuses.Any(s => s.Kind == TerminalStatusKind.Overflow));
        var dropped = statuses.First(s => s.Kind == TerminalStatusKind.Overflow);
        Assert.Equal("1", dropped.Code);

        // Consumer reads what is still buffered: 2 and 3. Then the next frame triggers the replay of the lost chunk.
        Assert.Equal(2, (await Eventually.NextAsync(source.Output)).Seq);
        Assert.Equal(3, (await Eventually.NextAsync(source.Output)).Seq);
        wire.Push(Out(4, 4));
        wire.Push(Out(1, 1));
        wire.Push(Out(2, 2));
        wire.Push(Out(3, 3));
        wire.Push(Out(4, 4));

        var lost = await Eventually.NextAsync(source.Output);
        var live = await Eventually.NextAsync(source.Output);
        Assert.Equal((1L, true), (lost.Seq, lost.IsReplay));
        Assert.Equal((4L, false), (live.Seq, live.IsReplay));
        var replay = Assert.Single(wire.SentOf<TerminalReplay>());
        Assert.Equal(1, replay.FromSeq);
    }

    [Fact]
    public async Task Viewer_input_is_refused_and_nothing_is_sent()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire, writerHeld: true);
        var statuses = new ConcurrentQueue<TerminalSourceStatus>();
        source.StatusChanged += (_, s) => statuses.Enqueue(s);

        await source.WriteAsync(new byte[] { 0x61 }, TestContext.Current.CancellationToken);

        Assert.Empty(wire.SentOf<TerminalInput>());
        Assert.Contains(statuses, s => s.Kind == TerminalStatusKind.WriteRefused);
    }

    [Fact]
    public async Task Granted_control_enables_input_and_release_disables_it()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire, writerHeld: true);

        await source.TakeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("take", Assert.Single(wire.SentOf<TerminalControl>()).Action);

        wire.Push(new TerminalControl(Id, "granted"));
        await Eventually.TrueAsync(() => source.CanWrite);
        await source.WriteAsync(new byte[] { 0x62 }, TestContext.Current.CancellationToken);
        Assert.Equal([0x62], wire.SentOf<TerminalInput>().Single().Bytes);

        await source.ReleaseAsync(TestContext.Current.CancellationToken);
        Assert.False(source.CanWrite);
        Assert.Equal("release", wire.SentOf<TerminalControl>().Last().Action);
    }

    [Fact]
    public async Task Denied_control_keeps_the_viewer_read_only()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire, writerHeld: true);
        var statuses = new ConcurrentQueue<TerminalSourceStatus>();
        source.StatusChanged += (_, s) => statuses.Enqueue(s);

        wire.Push(new TerminalControl(Id, "denied"));
        await Eventually.TrueAsync(() => statuses.Any(s => s.Kind == TerminalStatusKind.ControlChanged));

        Assert.False(source.CanWrite);
    }

    [Fact]
    public async Task Resize_is_sent_only_by_the_writer()
    {
        var viewerWire = new FakeWire();
        await using (var viewer = await OpenAsync(viewerWire, writerHeld: true))
        {
            await viewer.ResizeAsync(100, 40, TestContext.Current.CancellationToken);
            Assert.Empty(viewerWire.SentOf<TerminalResize>());
        }

        var writerWire = new FakeWire();
        await using var writer = await OpenAsync(writerWire, writerHeld: false);
        await writer.ResizeAsync(100, 40, TestContext.Current.CancellationToken);
        var resize = Assert.Single(writerWire.SentOf<TerminalResize>());
        Assert.Equal((100, 40), (resize.Cols, resize.Rows));
    }

    [Fact]
    public async Task Exit_completes_exited_and_output_and_stops_input()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire);

        wire.Push(Out(1, 1));
        wire.Push(new TerminalExit(Id, 3));

        Assert.Equal(3, await source.Exited);
        Assert.Equal(1L, (await Eventually.NextAsync(source.Output)).Seq);
        await foreach (var _ in source.Output.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            // Drain. The output completes after the exit.
        }

        Assert.False(source.CanWrite);
        await source.WriteAsync(new byte[] { 0x61 }, TestContext.Current.CancellationToken);
        Assert.Empty(wire.SentOf<TerminalInput>());
    }

    [Fact]
    public async Task Server_error_fails_closed_for_input_and_is_reported()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire);
        var statuses = new ConcurrentQueue<TerminalSourceStatus>();
        source.StatusChanged += (_, s) => statuses.Enqueue(s);

        wire.Push(new TerminalError(Id, "denied", "not allowed"));
        await Eventually.TrueAsync(() => statuses.Any(s => s.Kind == TerminalStatusKind.Error));

        var error = statuses.First(s => s.Kind == TerminalStatusKind.Error);
        Assert.Equal(("denied", "not allowed"), (error.Code, error.Message));
        Assert.False(source.CanWrite);

        await source.WriteAsync(new byte[] { 0x61 }, TestContext.Current.CancellationToken);
        await source.ResizeAsync(10, 10, TestContext.Current.CancellationToken);
        Assert.Empty(wire.SentOf<TerminalInput>());
        Assert.Empty(wire.SentOf<TerminalResize>());
    }

    [Fact]
    public async Task Frames_for_other_terminals_are_ignored()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire);

        wire.Push(new TerminalOutput("other", 1, [9]));
        wire.Push(Out(1, 1));

        var chunk = await Eventually.NextAsync(source.Output);
        Assert.Equal(1L, chunk.Seq);
        Assert.Equal(new byte[] { 1 }, chunk.Bytes.ToArray());
    }

    [Fact]
    public async Task Resubscribe_asks_for_replay_from_the_next_unseen_sequence()
    {
        var wire = new FakeWire();
        await using var source = await OpenAsync(wire);
        wire.Push(Out(1, 1));
        wire.Push(Out(2, 2));
        await Eventually.NextAsync(source.Output);
        await Eventually.NextAsync(source.Output);

        await source.ResubscribeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, wire.SentOf<TerminalReplay>().Single().FromSeq);
    }

    [Fact]
    public async Task Dispose_sends_TerminalClose_and_ends_the_source()
    {
        var wire = new FakeWire();
        var source = await OpenAsync(wire);

        await source.DisposeAsync();
        await source.DisposeAsync();

        Assert.Single(wire.SentOf<TerminalClose>());
        Assert.Equal(-1, await source.Exited);
        await foreach (var _ in source.Output.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.Fail("Output must complete after dispose.");
        }
    }

    [Fact]
    public async Task Dispose_after_exit_sends_no_close()
    {
        var wire = new FakeWire();
        var source = await OpenAsync(wire);
        wire.Push(new TerminalExit(Id, 0));
        await source.Exited;

        await source.DisposeAsync();

        Assert.Empty(wire.SentOf<TerminalClose>());
    }

    [Fact]
    public async Task Attach_fails_when_the_wire_closes_before_the_terminal_opens()
    {
        var wire = new FakeWire();
        var source = new RemoteTerminalSource(wire, Id);
        var open = source.OpenAsync(80, 24, TestContext.Current.CancellationToken);
        wire.Close();

        await Assert.ThrowsAsync<InvalidOperationException>(() => open);
        await source.DisposeAsync();
    }
}
