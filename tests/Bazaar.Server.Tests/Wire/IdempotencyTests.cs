// Tests pass no CancellationToken to fake-clock and loopback calls; the harness applies its own timeouts.
#pragma warning disable xUnit1051
using Bazaar.Contracts.Wire;
using Bazaar.Server.Tests.Wire.Harness;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Wire;

/// <summary>Idempotent request layer (W6): unit tests on the executor, then end to end over a socket.</summary>
public sealed class IdempotencyTests
{
    private static IdempotentRequestExecutor Executor(FakeClock clock, int retentionH = 24) =>
        new(Options.Create(new WireOptions { RequestIdRetentionH = retentionH }), clock);

    [Fact]
    public async Task Same_device_and_id_runs_the_handler_once()
    {
        var executor = Executor(new FakeClock());
        var runs = 0;
        ValueTask<WireReply> Run(CancellationToken _) => ValueTask.FromResult(WireReply.Ok(new byte[] { (byte)Interlocked.Increment(ref runs) }));

        var first = await executor.ExecuteAsync("d", "id1", Run, default);
        var second = await executor.ExecuteAsync("d", "id1", Run, default);

        Assert.Equal(1, runs);
        Assert.Equal(first.Payload.ToArray(), second.Payload.ToArray());
    }

    [Fact]
    public async Task Ids_are_scoped_to_the_device()
    {
        var executor = Executor(new FakeClock());
        var runs = 0;
        ValueTask<WireReply> Run(CancellationToken _)
        {
            runs++;
            return ValueTask.FromResult(WireReply.Ok());
        }

        await executor.ExecuteAsync("a", "id", Run, default);
        await executor.ExecuteAsync("b", "id", Run, default);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_retry_while_the_first_is_running_shares_its_result()
    {
        var executor = Executor(new FakeClock());
        var gate = new TaskCompletionSource();
        var runs = 0;
        async ValueTask<WireReply> Run(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
            return WireReply.Ok(new byte[] { 9 });
        }

        var first = executor.ExecuteAsync("d", "id", Run, default).AsTask();
        var second = executor.ExecuteAsync("d", "id", Run, default).AsTask();
        gate.SetResult();
        var replies = await Task.WhenAll(first, second);

        Assert.Equal(1, runs);
        Assert.All(replies, r => Assert.Equal(new byte[] { 9 }, r.Payload.ToArray()));
    }

    [Fact]
    public async Task Stored_errors_are_replayed_but_internal_faults_may_be_retried()
    {
        var executor = Executor(new FakeClock());
        var runs = 0;
        ValueTask<WireReply> Denied(CancellationToken _)
        {
            runs++;
            return ValueTask.FromResult(WireReply.Fail(WireErrorCodes.NotFound));
        }

        ValueTask<WireReply> Fault(CancellationToken _)
        {
            runs++;
            return ValueTask.FromResult(WireReply.Fail(WireErrorCodes.Internal));
        }

        await executor.ExecuteAsync("d", "denied", Denied, default);
        await executor.ExecuteAsync("d", "denied", Denied, default);
        await executor.ExecuteAsync("d", "fault", Fault, default);
        await executor.ExecuteAsync("d", "fault", Fault, default);

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task A_cancelled_first_attempt_is_forgotten_so_a_retry_runs_again()
    {
        var executor = Executor(new FakeClock());
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var first = executor.ExecuteAsync("d", "id", async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return WireReply.Ok();
        }, cts.Token).AsTask();
        await started.Task;
        var retry = executor.ExecuteAsync("d", "id", _ => ValueTask.FromResult(WireReply.Ok(new byte[] { 5 })), default).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(new byte[] { 5 }, (await retry).Payload.ToArray());
    }

    [Fact]
    public async Task Ids_expire_after_the_retention_window()
    {
        var clock = new FakeClock();
        var executor = Executor(clock);
        var runs = 0;
        ValueTask<WireReply> Run(CancellationToken _)
        {
            runs++;
            return ValueTask.FromResult(WireReply.Ok());
        }

        await executor.ExecuteAsync("d", "id", Run, default);
        clock.Advance(TimeSpan.FromHours(23));
        await executor.ExecuteAsync("d", "id", Run, default);
        Assert.Equal(1, runs);

        clock.Advance(TimeSpan.FromHours(2));
        await executor.ExecuteAsync("d", "other", Run, default);
        await executor.ExecuteAsync("d", "id", Run, default);

        Assert.Equal(3, runs);
        Assert.Equal(2, executor.Count);
    }

    [Fact]
    public async Task A_retried_gc_send_over_a_new_socket_returns_the_original_result_and_runs_once()
    {
        var posted = 0;
        var send = new DelegateHandler(WireTypes.GcSend, (c, _) =>
            ValueTask.FromResult(WireReply.Ok(c.Connection.Codec, new Pong($"n{Interlocked.Increment(ref posted)}"))));
        await using var server = await WireTestServer.StartAsync(handlers: [send]);

        await using var first = await WireTestClient.ConnectedAsync(server);
        var request = first.RequestOf(WireTypes.GcSend, new Ping(null), id: "send-1");
        await first.SendAsync(request);
        var original = first.Codec.DecodePayload<Pong>(await first.ReceiveAsync());

        // The link drops; the client reconnects as the same device and retries the same id.
        await using var second = await WireTestClient.ConnectedAsync(server);
        await second.SendAsync(request);
        var retried = second.Codec.DecodePayload<Pong>(await second.ReceiveAsync());

        Assert.Equal("n1", original.Nonce);
        Assert.Equal(original, retried);
        Assert.Equal(1, posted);
    }
}
