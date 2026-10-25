using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Testing;

namespace Bazaar.Client.Core.Tests.Wire;

public class WireClientTests : IAsyncLifetime
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _author = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly string _stream = WireStreams.Gc(_gc);

    private readonly FakeWireTransport _transport = new();
    private readonly FakeClock _clock = new();
    private readonly InMemoryCursorStore _cursors = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly WireClient _client;
    private Task _run = Task.CompletedTask;

    public WireClientTests()
    {
        _client = new WireClient(_transport, _transport.Codec, _cursors,
            new WireClientOptions { DeviceId = "dev-1", DeviceClass = "windows", ClientBuild = "test", Jitter = () => 1.0 }, _clock);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private static WireGcMessage Msg(long n, long ev, string text = "x") =>
        new(_gc, n, ev, Guid.NewGuid(), _author, text, DateTimeOffset.UnixEpoch, ev > n, [], null);

    private static Task Settle() => Task.Delay(50, TestContext.Current.CancellationToken);

    private void Start() => _run = _client.RunAsync(_cts.Token);

    private static async Task Until(Func<bool> condition, Action? tick = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            tick?.Invoke();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Advances the fake clock in one-second steps until the condition holds (reconnect backoff).</summary>
    private Task AdvanceUntil(Func<bool> condition) => Until(condition, () => _clock.Advance(TimeSpan.FromSeconds(1)));

    private async Task<WireGcMessage> Next() => await _client.Messages.ReadAsync(_cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Hello_then_subscribe_carries_the_stored_cursor()
    {
        _cursors.Save(new StreamCursor(_stream, 11, 39));
        await _client.SubscribeAsync(_stream, TestContext.Current.CancellationToken);
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());

        var hello = _transport.Codec.DecodePayload<Hello>(_transport.Latest.SentOfType(WireTypes.Hello).Single());
        Assert.Equal("dev-1", hello.DeviceId);
        Assert.Contains("json", hello.Codecs);
        var sub = _transport.Codec.DecodePayload<Subscribe>(_transport.Latest.SentOfType(WireTypes.Subscribe).Single());
        Assert.Equal(new StreamCursor(_stream, 11, 39), sub.Streams.Single());
    }

    [Fact]
    public async Task Messages_arrive_in_order_and_duplicates_are_dropped()
    {
        await _client.SubscribeAsync(_stream, TestContext.Current.CancellationToken);
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());

        var c = _transport.Latest;
        c.PushEvent(WireTypes.GcMessage, Msg(1, 1), _stream, 1);
        c.PushEvent(WireTypes.GcMessage, Msg(2, 2), _stream, 2);
        c.PushEvent(WireTypes.GcMessage, Msg(2, 2), _stream, 2); // echo or replay overlap
        c.PushEvent(WireTypes.GcMessage, Msg(1, 1), _stream, 1); // stale
        c.PushEvent(WireTypes.GcMessage, Msg(3, 3), _stream, 3);

        long first = (await Next()).N, second = (await Next()).N, third = (await Next()).N;
        Assert.Equal((1L, 2L, 3L), (first, second, third));
        Assert.False(_client.Messages.TryRead(out _));
        Assert.Equal(new StreamCursor(_stream, 3, 3), _cursors.Load(_stream));
    }

    [Fact]
    public async Task An_edit_repeats_n_with_a_higher_ev_and_is_delivered_once()
    {
        await _client.SubscribeAsync(_stream, TestContext.Current.CancellationToken);
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());

        var c = _transport.Latest;
        c.PushEvent(WireTypes.GcMessage, Msg(5, 5, "v1"), _stream, 5);
        c.PushEvent(WireTypes.GcMessage, Msg(5, 6, "v2"), _stream, 5);
        c.PushEvent(WireTypes.GcMessage, Msg(5, 6, "v2"), _stream, 5);

        Assert.Equal("v1", (await Next()).Text);
        Assert.Equal("v2", (await Next()).Text);
        await Settle();
        Assert.False(_client.Messages.TryRead(out _));
    }

    [Fact]
    public async Task Reconnect_waits_the_backoff_and_resumes_from_the_cursor()
    {
        await _client.SubscribeAsync(_stream, TestContext.Current.CancellationToken);
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());
        _transport.Latest.PushEvent(WireTypes.GcMessage, Msg(1, 1), _stream, 1);
        await Next();

        _transport.Latest.Close();
        await Settle();
        Assert.Equal(1, _transport.Count); // still inside the first backoff window

        await AdvanceUntil(() => _transport.Count == 2 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());
        var sub = _transport.Codec.DecodePayload<Subscribe>(_transport.Latest.SentOfType(WireTypes.Subscribe).Single());
        Assert.Equal(new StreamCursor(_stream, 1, 1), sub.Streams.Single());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(30, 60)]
    public void Backoff_doubles_and_is_capped(int attempt, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), _client.BackoffDelay(attempt));
    }

    [Fact]
    public void Backoff_jitter_stays_between_half_and_full_delay()
    {
        var low = new WireClient(_transport, _transport.Codec, _cursors,
            new WireClientOptions { DeviceId = "d", DeviceClass = "w", ClientBuild = "t", Jitter = () => 0.0 }, _clock);

        Assert.Equal(TimeSpan.FromSeconds(0.5), low.BackoffDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(30), low.BackoffDelay(10));
    }

    [Fact]
    public async Task Unreachable_server_is_retried_with_backoff()
    {
        _transport.FailNextConnects = 2;
        Start();

        await AdvanceUntil(() => _transport.Count == 1);
        Assert.Equal(1, _client.ConnectionCount);
    }

    [Fact]
    public async Task Lagged_resubscribes_from_the_cursor()
    {
        await _client.SubscribeAsync(_stream, TestContext.Current.CancellationToken);
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());
        var c = _transport.Latest;
        c.PushEvent(WireTypes.GcMessage, Msg(4, 4), _stream, 4);
        await Next();

        c.PushEvent(WireTypes.Lagged, new Lagged(_stream, 4), _stream);

        await Until(() => c.SentOfType(WireTypes.Subscribe).Count() == 2);
        var sub = _transport.Codec.DecodePayload<Subscribe>(c.SentOfType(WireTypes.Subscribe).Last());
        Assert.Equal(new StreamCursor(_stream, 4, 4), sub.Streams.Single());
    }

    [Fact]
    public async Task Resync_advances_the_cursor_applies_the_tail_and_notifies()
    {
        await _client.SubscribeAsync(_stream, TestContext.Current.CancellationToken);
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());

        _transport.Latest.PushEvent(WireTypes.Resync, new Resync(_stream, 5000, 5100, 3, [Msg(4999, 5099), Msg(5000, 5100)]), _stream);

        var notice = await _client.Resyncs.ReadAsync(_cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(3, notice.Unread);
        Assert.Equal(4999, (await Next()).N);
        Assert.Equal(5000, (await Next()).N);
        Assert.Equal(new StreamCursor(_stream, 5000, 5100), _cursors.Load(_stream));
    }

    [Fact]
    public async Task Request_is_answered_and_decoded()
    {
        Start();
        await Until(() => _transport.Count == 1 && _client.ConnectionCount == 1);
        await Until(() => _transport.Latest.SentOfType(WireTypes.Hello).Any());
        var c = _transport.Latest;
        await Settle();

        var task = _client.RequestAsync<GcSend, GcSendResult>(WireTypes.GcSend, new GcSend(_gc, "hi", null, null), TestContext.Current.CancellationToken);
        await Until(() => c.SentOfType(WireTypes.GcSend).Any());
        var req = c.SentOfType(WireTypes.GcSend).Single();
        c.Push(new Envelope(1, WireTypes.GcSend, EnvelopeKind.Response, null, req.Id, null, null, DateTimeOffset.UnixEpoch,
            _transport.Codec.EncodePayload(new GcSendResult(8, 9))));

        Assert.Equal(new GcSendResult(8, 9), await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Error_answer_throws_with_the_server_code()
    {
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Hello).Any());
        await Settle();
        var c = _transport.Latest;

        var task = _client.RequestAsync<GcSend, GcSendResult>(WireTypes.GcSend, new GcSend(_gc, "hi", null, null), TestContext.Current.CancellationToken);
        await Until(() => c.SentOfType(WireTypes.GcSend).Any());
        c.Push(new Envelope(1, WireTypes.GcSend, EnvelopeKind.Error, null, c.SentOfType(WireTypes.GcSend).Single().Id, null, null, DateTimeOffset.UnixEpoch,
            _transport.Codec.EncodePayload(new WireError(WireErrorCodes.RateLimited, null, 1500))));

        var ex = await Assert.ThrowsAsync<WireRequestException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(WireErrorCodes.RateLimited, ex.Error.Code);
        Assert.Equal(1500, ex.Error.RetryAfterMs);
    }

    [Fact]
    public async Task Unanswered_request_is_resent_with_the_same_id_after_reconnect()
    {
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Hello).Any());
        await Settle();
        var first = _transport.Latest;

        var task = _client.RequestAsync<GcSend, GcSendResult>(WireTypes.GcSend, new GcSend(_gc, "hi", null, null), TestContext.Current.CancellationToken);
        await Until(() => first.SentOfType(WireTypes.GcSend).Any());
        string id = first.SentOfType(WireTypes.GcSend).Single().Id!;
        first.Close();

        await AdvanceUntil(() => _transport.Count == 2 && _transport.Latest.SentOfType(WireTypes.GcSend).Any());
        var second = _transport.Latest;
        Assert.Equal(id, second.SentOfType(WireTypes.GcSend).Single().Id);
        second.Push(new Envelope(1, WireTypes.GcSend, EnvelopeKind.Response, null, id, null, null, DateTimeOffset.UnixEpoch,
            _transport.Codec.EncodePayload(new GcSendResult(1, 1))));

        Assert.Equal(new GcSendResult(1, 1), await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(WireCloseCodes.UpgradeRequired, WireClientStopReason.UpgradeRequired)]
    [InlineData(WireCloseCodes.AuthFailed, WireClientStopReason.AuthFailed)]
    [InlineData(WireCloseCodes.Revoked, WireClientStopReason.Revoked)]
    public async Task Terminal_close_codes_stop_the_client_without_reconnecting(int code, WireClientStopReason reason)
    {
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Hello).Any());

        _transport.Latest.Close(code);

        await _run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(reason, _client.Stopped);
        Assert.Equal(1, _transport.Count);
    }

    [Fact]
    public async Task Welcome_with_upgrade_required_stops_the_client()
    {
        _transport.WelcomeUpgradeRequired = true;
        Start();

        await _run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(WireClientStopReason.UpgradeRequired, _client.Stopped);
    }

    [Fact]
    public async Task Silent_server_is_dropped_after_the_heartbeat_timeout_and_reconnected()
    {
        Start();
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Hello).Any());

        await AdvanceUntil(() => _transport.Count == 2);

        Assert.Null(_client.Stopped);
        Assert.NotEmpty(_transport.Connections[0].SentOfType(WireTypes.Ping));
    }
}
