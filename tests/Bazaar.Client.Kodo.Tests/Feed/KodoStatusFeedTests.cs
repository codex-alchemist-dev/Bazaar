using Bazaar.Client.Kodo;
using Bazaar.Contracts.Kodo;

namespace Bazaar.Client.Kodo.Tests.Feed;

public class KodoStatusFeedTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSource(Func<KodoStatusDto> next) : IKodoStatusSource
    {
        public Task<KodoStatusDto> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(next());
    }

    private sealed class ThrowingSource(Exception error) : IKodoStatusSource
    {
        public Task<KodoStatusDto> GetStatusAsync(CancellationToken cancellationToken) => Task.FromException<KodoStatusDto>(error);
    }

    private static KodoStatusDto Dto(string state, string? underlying = null, params KodoControlDto[] controls) =>
        new(state, underlying, 88, _now, controls);

    [Theory]
    [InlineData("healthy", KodoHealthState.Healthy)]
    [InlineData("degraded", KodoHealthState.Degraded)]
    [InlineData("failing", KodoHealthState.Failing)]
    [InlineData("recovering", KodoHealthState.Recovering)]
    [InlineData("frozen", KodoHealthState.Frozen)]
    public void Every_wire_state_maps_to_its_state(string wire, KodoHealthState expected)
    {
        Assert.True(KodoStatusMapper.TryMap(Dto(wire), out var snapshot, out var error));
        Assert.Null(error);
        Assert.Equal(expected, snapshot!.State);
    }

    [Fact]
    public void Wire_controls_map_and_keep_the_server_flags()
    {
        var dto = Dto("frozen", "failing",
            new KodoControlDto("restart", false, "Incident mode is active; risky actions are blocked"),
            new KodoControlDto("incidentExit", true, null));

        Assert.True(KodoStatusMapper.TryMap(dto, out var snapshot, out _));
        Assert.Equal(KodoHealthState.Failing, snapshot!.UnderlyingState);
        Assert.Equal(KodoControl.Restart, snapshot.Controls[0].Control);
        Assert.False(snapshot.Controls[0].Enabled);
        Assert.Equal("Incident mode is active; risky actions are blocked", snapshot.Controls[0].DisabledReason);
        Assert.Equal(KodoControl.IncidentExit, snapshot.Controls[1].Control);
        Assert.True(snapshot.Controls[1].Enabled);
    }

    [Fact]
    public void An_unknown_state_is_refused_not_guessed()
    {
        Assert.False(KodoStatusMapper.TryMap(Dto("Healthy"), out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.Equal("Unknown Kodo state 'Healthy'.", error);
    }

    [Fact]
    public void An_unknown_control_is_refused_not_dropped()
    {
        Assert.False(KodoStatusMapper.TryMap(Dto("healthy", null, new KodoControlDto("shutdown", true, null)), out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.Equal("Unknown Kodo control 'shutdown'.", error);
    }

    [Fact]
    public async Task A_good_read_renders_and_becomes_the_last_good_row()
    {
        var feed = new KodoStatusFeed(new FakeSource(() => Dto("degraded")), () => _now);

        var row = await feed.RefreshAsync(CancellationToken.None);

        Assert.NotNull(row);
        Assert.Equal("Degraded", row!.StateWord);
        Assert.Same(row, feed.LastGood);
        Assert.Null(feed.LastError);
    }

    [Fact]
    public async Task A_refused_read_keeps_the_last_good_row_and_says_why()
    {
        var good = new Queue<KodoStatusDto>([Dto("healthy"), Dto("bogus")]);
        var feed = new KodoStatusFeed(new FakeSource(() => good.Dequeue()), () => _now);

        var first = await feed.RefreshAsync(CancellationToken.None);
        var second = await feed.RefreshAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal("Healthy", second!.StateWord);
        Assert.Equal("Unknown Kodo state 'bogus'.", feed.LastError);
    }

    [Fact]
    public async Task An_unreachable_server_keeps_the_last_good_row()
    {
        var calls = 0;
        var feed = new KodoStatusFeed(new FakeSource(() =>
        {
            calls++;
            return calls == 1 ? Dto("healthy") : throw new HttpRequestException("connection refused");
        }), () => _now);

        var first = await feed.RefreshAsync(CancellationToken.None);
        var second = await feed.RefreshAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.StartsWith("Kodo is unreachable", feed.LastError);
    }

    [Fact]
    public async Task A_cancelled_read_is_not_swallowed()
    {
        var feed = new KodoStatusFeed(new ThrowingSource(new OperationCanceledException()), () => _now);

        await Assert.ThrowsAsync<OperationCanceledException>(() => feed.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public void Null_collaborators_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new KodoStatusFeed(null!, () => _now));
        Assert.Throws<ArgumentNullException>(() => new KodoStatusFeed(new FakeSource(() => Dto("healthy")), null!));
    }
}
