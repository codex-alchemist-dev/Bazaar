using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.EngineDispatch.Tests;

public sealed class EngineRelayBrokerTests
{
    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string _device = "desktop-1";

    private sealed class Grants : IEngineGrantReader
    {
        public bool Covers = true;

        public ValueTask<bool> CoversAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, string grantId, CancellationToken ct) =>
            ValueTask.FromResult(Covers);
    }

    private sealed class Taint : ISessionTaintReader
    {
        public ValueTask<bool> IsHardTaintedAsync(Guid agentId, Guid? sessionId, CancellationToken ct) => ValueTask.FromResult(false);
    }

    private sealed class Recorder : IEngineGcRecorder
    {
        public Task RecordAsync(EngineToolCallRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Audit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Targets : IRelayTargetDirectory
    {
        public string? Device = EngineRelayBrokerTests._device;
        public Exception? Throw;
        public int Calls;

        public ValueTask<string?> FindTargetDeviceAsync(CancellationToken ct)
        {
            Calls++;
            return Throw is not null ? throw Throw : ValueTask.FromResult(Device);
        }
    }

    private sealed class Publisher : IRelayPublisher
    {
        public readonly ConcurrentQueue<(string Device, EngineRelayRequestEvent Event)> Sent = new();
        public Exception? Throw;

        public ValueTask PublishAsync(string deviceId, EngineRelayRequestEvent relayEvent, CancellationToken ct)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Sent.Enqueue((deviceId, relayEvent));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public Grants Grants = new();
        public Targets Targets = new();
        public Publisher Publisher = new();

        public EngineRelayBroker Build(TimeSpan? timeout = null) =>
            new(
                new EngineToolDispatcher(Grants, new Taint(), new Recorder(), new Audit(), new EngineDispatchOptions { RobloxExecOwnerApproval = false }),
                Targets,
                Publisher,
                new EngineRelayOptions { Timeout = timeout ?? TimeSpan.FromSeconds(30) });
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static EngineDispatchRequest Req(string tool = "multi_edit", string? grant = "g1", string args = "{}") =>
        new(_agent, "roblox", grant, "studio-1", tool, Json(args));

    private static EngineRelayCallResult Answer(string grant = "g1", string payload = "{\"done\":true}") => new(grant, true, Json(payload));

    private static async Task<EngineRelayRequestEvent> NextEventAsync(Publisher publisher, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (publisher.Sent.TryDequeue(out var sent))
            {
                return sent.Event;
            }

            await Task.Delay(5, ct);
        }

        throw new OperationCanceledException(ct);
    }

    [Fact]
    public async Task Happy_path_publishes_to_the_target_and_returns_the_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build();

        var call = broker.RelayAsync(Req(args: "{\"x\":1}"), ct);
        var evt = await NextEventAsync(rig.Publisher, ct);

        Assert.Equal("multi_edit", evt.Call.Tool);
        Assert.Equal("studio-1", evt.Call.StudioId);
        Assert.Equal("g1", evt.Call.GrantId);
        Assert.Equal(1, evt.Call.Args.GetProperty("x").GetInt32());
        Assert.True(evt.Deadline > DateTimeOffset.UtcNow);
        Assert.Equal(1, broker.PendingCount);

        Assert.Equal(EngineRelaySubmitStatus.Accepted, broker.SubmitResult(_device, evt.RelayId, Answer()));
        var outcome = await call;

        Assert.Equal(EngineRelayStatus.Completed, outcome.Status);
        Assert.True(outcome.Result!.Ok);
        Assert.True(outcome.Result.Payload.GetProperty("done").GetBoolean());
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task Missing_args_are_published_as_an_empty_object()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build();

        var call = broker.RelayAsync(new EngineDispatchRequest(_agent, "roblox", null, "studio-1", "script_read", default), ct);
        var evt = await NextEventAsync(rig.Publisher, ct);
        Assert.Equal(JsonValueKind.Object, evt.Call.Args.ValueKind);
        broker.SubmitResult(_device, evt.RelayId, Answer(grant: ""));
        Assert.Equal(EngineRelayStatus.Completed, (await call).Status);
    }

    [Fact]
    public async Task Denied_by_the_dispatcher_is_never_published()
    {
        var rig = new Rig { Grants = new Grants { Covers = false } };
        var broker = rig.Build();

        var outcome = await broker.RelayAsync(Req(), TestContext.Current.CancellationToken);

        Assert.Equal(EngineRelayStatus.Denied, outcome.Status);
        Assert.Equal(EngineDispatchReason.GrantNotCovering, outcome.DenyReason);
        Assert.Empty(rig.Publisher.Sent);
        Assert.Equal(0, rig.Targets.Calls);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task Unknown_tool_is_denied_and_never_published()
    {
        var rig = new Rig();
        var outcome = await rig.Build().RelayAsync(Req("format_disk"), TestContext.Current.CancellationToken);
        Assert.Equal(EngineRelayStatus.Denied, outcome.Status);
        Assert.Empty(rig.Publisher.Sent);
    }

    [Fact]
    public async Task No_connected_target_fails_unavailable_at_once()
    {
        var rig = new Rig();
        rig.Targets.Device = null;
        var broker = rig.Build(TimeSpan.FromMinutes(5));

        var outcome = await broker.RelayAsync(Req(), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(EngineRelayStatus.Unavailable, outcome.Status);
        Assert.Empty(rig.Publisher.Sent);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task A_failing_directory_fails_closed_as_unavailable()
    {
        var rig = new Rig();
        rig.Targets.Throw = new InvalidOperationException("boom");
        var outcome = await rig.Build().RelayAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Equal(EngineRelayStatus.Unavailable, outcome.Status);
        Assert.Empty(rig.Publisher.Sent);
    }

    [Fact]
    public async Task A_failing_publish_is_unavailable_and_leaves_nothing_pending()
    {
        var rig = new Rig();
        rig.Publisher.Throw = new InvalidOperationException("device gone");
        var broker = rig.Build();

        var outcome = await broker.RelayAsync(Req(), TestContext.Current.CancellationToken);

        Assert.Equal(EngineRelayStatus.Unavailable, outcome.Status);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task Timeout_returns_timeout_and_a_late_answer_is_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build(TimeSpan.FromMilliseconds(100));

        var outcome = await broker.RelayAsync(Req(), ct);
        var evt = await NextEventAsync(rig.Publisher, ct);

        Assert.Equal(EngineRelayStatus.Timeout, outcome.Status);
        Assert.Equal(0, broker.PendingCount);
        Assert.Equal(EngineRelaySubmitStatus.Unknown, broker.SubmitResult(_device, evt.RelayId, Answer()));
    }

    [Fact]
    public async Task Caller_cancellation_throws_and_removes_the_pending_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var call = broker.RelayAsync(Req(), cts.Token);
        var evt = await NextEventAsync(rig.Publisher, ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, broker.PendingCount);
        Assert.Equal(EngineRelaySubmitStatus.Unknown, broker.SubmitResult(_device, evt.RelayId, Answer()));
    }

    [Fact]
    public async Task An_answer_from_another_device_is_ignored_and_the_right_device_still_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build();

        var call = broker.RelayAsync(Req(), ct);
        var evt = await NextEventAsync(rig.Publisher, ct);

        Assert.Equal(EngineRelaySubmitStatus.ForeignDevice, broker.SubmitResult("phone-9", evt.RelayId, Answer()));
        Assert.False(call.IsCompleted);
        Assert.Equal(1, broker.PendingCount);

        Assert.Equal(EngineRelaySubmitStatus.Accepted, broker.SubmitResult(_device, evt.RelayId, Answer()));
        Assert.Equal(EngineRelayStatus.Completed, (await call).Status);
    }

    [Fact]
    public async Task A_duplicate_answer_is_ignored_and_the_first_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build();

        var call = broker.RelayAsync(Req(), ct);
        var evt = await NextEventAsync(rig.Publisher, ct);

        Assert.Equal(EngineRelaySubmitStatus.Accepted, broker.SubmitResult(_device, evt.RelayId, Answer(payload: "{\"n\":1}")));
        Assert.Equal(EngineRelaySubmitStatus.Unknown, broker.SubmitResult(_device, evt.RelayId, Answer(payload: "{\"n\":2}")));

        var outcome = await call;
        Assert.Equal(1, outcome.Result!.Payload.GetProperty("n").GetInt32());
    }

    [Fact]
    public void An_unknown_relay_id_is_ignored()
    {
        var broker = new Rig().Build();
        Assert.Equal(EngineRelaySubmitStatus.Unknown, broker.SubmitResult(_device, Guid.NewGuid(), Answer()));
    }

    [Fact]
    public async Task An_answer_with_a_different_grant_id_is_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build();

        var call = broker.RelayAsync(Req(), ct);
        var evt = await NextEventAsync(rig.Publisher, ct);

        Assert.Equal(EngineRelaySubmitStatus.GrantMismatch, broker.SubmitResult(_device, evt.RelayId, Answer(grant: "other")));
        Assert.Equal(1, broker.PendingCount);
        broker.SubmitResult(_device, evt.RelayId, Answer());
        Assert.Equal(EngineRelayStatus.Completed, (await call).Status);
    }

    [Fact]
    public async Task Connection_close_fails_only_that_devices_pending_relays()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build();

        var first = broker.RelayAsync(Req(), ct);
        var evt = await NextEventAsync(rig.Publisher, ct);
        rig.Targets.Device = "desktop-2";
        var second = broker.RelayAsync(Req(), ct);
        var evt2 = await NextEventAsync(rig.Publisher, ct);

        Assert.Equal(1, broker.DeviceClosed(_device));

        var failed = await first;
        Assert.Equal(EngineRelayStatus.Unavailable, failed.Status);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, broker.PendingCount);
        Assert.Equal(EngineRelaySubmitStatus.Unknown, broker.SubmitResult(_device, evt.RelayId, Answer()));

        broker.SubmitResult("desktop-2", evt2.RelayId, Answer());
        Assert.Equal(EngineRelayStatus.Completed, (await second).Status);
    }

    [Fact]
    public async Task Concurrent_relays_correlate_each_answer_to_its_own_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broker = rig.Build();

        var calls = Enumerable.Range(0, 25)
            .Select(i => broker.RelayAsync(Req(args: $"{{\"i\":{i}}}"), ct))
            .ToList();

        var events = new List<EngineRelayRequestEvent>();
        for (var i = 0; i < calls.Count; i++)
        {
            events.Add(await NextEventAsync(rig.Publisher, ct));
        }

        Assert.Equal(25, events.Select(e => e.RelayId).Distinct().Count());

        // Answer in reverse arrival order, echoing each call's own argument.
        foreach (var evt in Enumerable.Reverse(events))
        {
            var i = evt.Call.Args.GetProperty("i").GetInt32();
            Assert.Equal(EngineRelaySubmitStatus.Accepted, broker.SubmitResult(_device, evt.RelayId, Answer(payload: $"{{\"echo\":{i}}}")));
        }

        var outcomes = await Task.WhenAll(calls);
        for (var i = 0; i < outcomes.Length; i++)
        {
            Assert.Equal(i, outcomes[i].Result!.Payload.GetProperty("echo").GetInt32());
        }

        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public void Timeout_option_falls_back_and_clamps()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new EngineRelayOptions().EffectiveTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), new EngineRelayOptions { Timeout = TimeSpan.Zero }.EffectiveTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), new EngineRelayOptions { Timeout = TimeSpan.FromSeconds(5) }.EffectiveTimeout);
        Assert.Equal(EngineRelayOptions.MaxTimeout, new EngineRelayOptions { Timeout = TimeSpan.FromHours(3) }.EffectiveTimeout);
    }
}
