using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Bazaar.Client.Studio.Abstractions;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Studio.Tests;

public sealed class StudioRelayWireClientTests
{
    private sealed class ChannelSource : IRelayEventSource
    {
        public Channel<EngineRelayRequestEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<EngineRelayRequestEvent>();
        public Exception? ThrowAfterDrain;

        public async IAsyncEnumerable<EngineRelayRequestEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var evt in Channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }

            if (ThrowAfterDrain is not null)
            {
                throw ThrowAfterDrain;
            }
        }
    }

    private sealed class Sender : IRelayResultSender
    {
        public readonly ConcurrentQueue<EngineRelayResultRequest> Sent = new();
        public Func<EngineRelayResultRequest, Exception?>? Fail;

        public ValueTask SendAsync(EngineRelayResultRequest result, CancellationToken cancellationToken)
        {
            if (Fail?.Invoke(result) is { } ex)
            {
                throw ex;
            }

            Sent.Enqueue(result);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Relay : IStudioRelay
    {
        public Func<StudioRelayRequest, CancellationToken, Task<StudioRelayResult>> Handler { get; set; } =
            (r, _) => Task.FromResult(new StudioRelayResult(r.GrantId, true, Json("{\"ok\":1}")));

        public int Calls;
        private int _active;
        public int MaxActive;

        public async Task<StudioRelayResult> RelayAsync(StudioRelayRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var now = Interlocked.Increment(ref _active);
            int seen;
            while ((seen = MaxActive) < now && Interlocked.CompareExchange(ref MaxActive, now, seen) != seen)
            {
            }

            try
            {
                return await Handler(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static EngineRelayRequestEvent Evt(TimeSpan? validFor = null, string tool = "script_read", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), DateTimeOffset.UtcNow + (validFor ?? TimeSpan.FromSeconds(30)), new EngineRelayCall("g1", "studio-A", tool, Json("{}")));

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static (StudioRelayWireClient Client, ChannelSource Source, Sender Sender, Relay Relay) Make(StudioRelayWireOptions? options = null)
    {
        var source = new ChannelSource();
        var sender = new Sender();
        var relay = new Relay();
        return (new StudioRelayWireClient(source, sender, relay, options), source, sender, relay);
    }

    [Fact]
    public async Task Runs_the_call_and_answers_with_the_relay_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make();
        var evt = Evt(tool: "script_read");
        await source.Channel.Writer.WriteAsync(evt, ct);
        source.Channel.Writer.Complete();

        await client.RunAsync(ct);

        var answer = Assert.Single(sender.Sent);
        Assert.Equal(evt.RelayId, answer.RelayId);
        Assert.True(answer.Result.Ok);
        Assert.Equal("g1", answer.Result.GrantId);
        Assert.Equal(1, answer.Result.Payload.GetProperty("ok").GetInt32());
        Assert.Equal(1, relay.Calls);
    }

    [Fact]
    public async Task A_throwing_relay_is_answered_with_an_error_payload_and_the_loop_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make();
        var first = true;
        relay.Handler = (r, _) =>
        {
            if (first)
            {
                first = false;
                throw new InvalidOperationException("secret Studio content");
            }

            return Task.FromResult(new StudioRelayResult(r.GrantId, true, Json("{}")));
        };
        var bad = Evt();
        var good = Evt();
        var run = client.RunAsync(ct);
        await source.Channel.Writer.WriteAsync(bad, ct);
        await WaitForAsync(() => relay.Calls == 1, ct);
        await source.Channel.Writer.WriteAsync(good, ct);
        source.Channel.Writer.Complete();

        await run;

        var answers = sender.Sent.ToDictionary(a => a.RelayId);
        Assert.Equal(2, answers.Count);
        var failed = answers[bad.RelayId].Result;
        Assert.False(failed.Ok);
        Assert.True(StudioRelayError.TryFromPayload(failed.Payload, out var error));
        Assert.Equal(StudioRelayErrorCode.Unavailable, error!.Code);
        Assert.DoesNotContain("secret", failed.Payload.GetRawText());
        Assert.True(answers[good.RelayId].Result.Ok);
    }

    [Fact]
    public async Task A_failed_relay_result_is_passed_through_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make();
        relay.Handler = (r, _) => Task.FromResult(new StudioRelayResult(r.GrantId, false, new StudioRelayError(StudioRelayErrorCode.StudioUnknown, "no such studio").ToPayload()));
        await source.Channel.Writer.WriteAsync(Evt(), ct);
        source.Channel.Writer.Complete();

        await client.RunAsync(ct);

        var answer = Assert.Single(sender.Sent);
        Assert.False(answer.Result.Ok);
        Assert.True(StudioRelayError.TryFromPayload(answer.Result.Payload, out var error));
        Assert.Equal(StudioRelayErrorCode.StudioUnknown, error!.Code);
    }

    [Fact]
    public async Task An_event_past_its_deadline_is_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make();
        await source.Channel.Writer.WriteAsync(Evt(validFor: TimeSpan.FromSeconds(-1)), ct);
        source.Channel.Writer.Complete();

        await client.RunAsync(ct);

        Assert.Empty(sender.Sent);
        Assert.Equal(0, relay.Calls);
    }

    [Fact]
    public async Task A_call_still_running_at_the_deadline_is_answered_with_a_timeout_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make();
        relay.Handler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return null!;
        };
        await source.Channel.Writer.WriteAsync(Evt(validFor: TimeSpan.FromMilliseconds(200)), ct);
        source.Channel.Writer.Complete();

        await client.RunAsync(ct);

        var answer = Assert.Single(sender.Sent);
        Assert.False(answer.Result.Ok);
        Assert.True(StudioRelayError.TryFromPayload(answer.Result.Payload, out var error));
        Assert.Equal(StudioRelayErrorCode.Timeout, error!.Code);
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make(new StudioRelayWireOptions { MaxConcurrent = 2 });
        var gate = new TaskCompletionSource();
        relay.Handler = async (r, _) =>
        {
            await gate.Task;
            return new StudioRelayResult(r.GrantId, true, Json("{}"));
        };
        for (var i = 0; i < 5; i++)
        {
            await source.Channel.Writer.WriteAsync(Evt(), ct);
        }

        source.Channel.Writer.Complete();
        var run = client.RunAsync(ct);

        await WaitForAsync(() => relay.Calls == 2, ct);
        await Task.Delay(100, ct);
        Assert.Equal(2, relay.Calls);

        gate.SetResult();
        await run;

        Assert.Equal(5, sender.Sent.Count);
        Assert.Equal(2, relay.MaxActive);
    }

    [Fact]
    public async Task Events_run_concurrently_up_to_the_bound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make(new StudioRelayWireOptions { MaxConcurrent = 3 });
        var gate = new TaskCompletionSource();
        relay.Handler = async (r, _) =>
        {
            await gate.Task;
            return new StudioRelayResult(r.GrantId, true, Json("{}"));
        };
        for (var i = 0; i < 3; i++)
        {
            await source.Channel.Writer.WriteAsync(Evt(), ct);
        }

        source.Channel.Writer.Complete();
        var run = client.RunAsync(ct);

        await WaitForAsync(() => relay.Calls == 3, ct);
        gate.SetResult();
        await run;

        Assert.Equal(3, relay.MaxActive);
        Assert.Equal(3, sender.Sent.Count);
    }

    [Fact]
    public async Task A_failing_send_is_reported_and_does_not_stop_the_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        var errors = new ConcurrentQueue<Exception>();
        var (client, source, sender, _) = Make(new StudioRelayWireOptions { OnError = errors.Enqueue });
        var first = Evt();
        var second = Evt();
        sender.Fail = r => r.RelayId == first.RelayId ? new IOException("socket closed") : null;
        await source.Channel.Writer.WriteAsync(first, ct);
        await source.Channel.Writer.WriteAsync(second, ct);
        source.Channel.Writer.Complete();

        await client.RunAsync(ct);

        Assert.Equal(second.RelayId, Assert.Single(sender.Sent).RelayId);
        Assert.IsType<IOException>(Assert.Single(errors));
    }

    [Fact]
    public async Task A_broken_event_source_ends_the_loop_without_throwing()
    {
        var ct = TestContext.Current.CancellationToken;
        var errors = new ConcurrentQueue<Exception>();
        var (client, source, sender, _) = Make(new StudioRelayWireOptions { OnError = errors.Enqueue });
        source.ThrowAfterDrain = new InvalidOperationException("stream broke");
        await source.Channel.Writer.WriteAsync(Evt(), ct);
        source.Channel.Writer.Complete();

        await client.RunAsync(ct);

        Assert.Single(sender.Sent);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
    }

    [Fact]
    public async Task Cancellation_stops_the_loop_quietly_and_sends_no_answer_for_calls_in_flight()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make();
        relay.Handler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return null!;
        };
        await source.Channel.Writer.WriteAsync(Evt(), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = client.RunAsync(cts.Token);
        await WaitForAsync(() => relay.Calls == 1, ct);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_replayed_event_still_in_flight_runs_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, source, sender, relay) = Make();
        var gate = new TaskCompletionSource();
        relay.Handler = async (r, _) =>
        {
            await gate.Task;
            return new StudioRelayResult(r.GrantId, true, Json("{}"));
        };
        var evt = Evt();
        await source.Channel.Writer.WriteAsync(evt, ct);
        await source.Channel.Writer.WriteAsync(evt, ct);
        source.Channel.Writer.Complete();
        var run = client.RunAsync(ct);

        await WaitForAsync(() => relay.Calls >= 1, ct);
        await Task.Delay(100, ct);
        gate.SetResult();
        await run;

        Assert.Equal(1, relay.Calls);
        Assert.Single(sender.Sent);
    }
}
