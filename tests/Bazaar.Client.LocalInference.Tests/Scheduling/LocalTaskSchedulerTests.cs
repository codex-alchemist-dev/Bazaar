using Bazaar.Client.LocalInference.Scheduling;

namespace Bazaar.Client.LocalInference.Tests.Scheduling;

public class LocalTaskSchedulerTests
{
    private static LocalTaskRequest Request(LocalTaskKind kind, string key) => new(kind, key, 64, key);

    private static Task<LocalTaskResult> Submit(LocalTaskScheduler scheduler, LocalTaskRequest request) =>
        scheduler.SubmitAsync(request, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Runs_queued_requests_by_priority()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        var blocker = Submit(scheduler, Request(LocalTaskKind.UiAssist, "blocker"));
        (await backend.NextStartedAsync()).Complete("blocker");
        await blocker;

        var chip = Submit(scheduler, Request(LocalTaskKind.ChipSummary, "chip"));
        var rewrite = Submit(scheduler, Request(LocalTaskKind.PromptRewrite, "rewrite"));
        var voice = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "voice"));
        var ui = Submit(scheduler, Request(LocalTaskKind.UiAssist, "ui"));

        var order = new List<LocalTaskKind>();
        for (var i = 0; i < 4; i++)
        {
            var call = await backend.NextStartedAsync();
            order.Add(call.Request.Kind);
            call.Complete(call.Request.Input);
        }

        Assert.Equal(
            [LocalTaskKind.VoiceCleanup, LocalTaskKind.UiAssist, LocalTaskKind.PromptRewrite, LocalTaskKind.ChipSummary],
            order);
        Assert.Equal("voice", (await voice).Text);
        Assert.Equal("ui", (await ui).Text);
        Assert.Equal("rewrite", (await rewrite).Text);
        Assert.Equal("chip", (await chip).Text);
    }

    [Fact]
    public async Task Keeps_first_in_first_out_within_a_priority()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        var blocker = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "blocker"));
        var first = await backend.NextStartedAsync();
        var a = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "a"));
        var b = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "b"));
        var c = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "c"));
        first.Complete("blocker");
        await blocker;

        var seen = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var call = await backend.NextStartedAsync();
            seen.Add(call.Request.Input);
            call.Complete(call.Request.Input);
        }

        Assert.Equal(["a", "b", "c"], seen);
        Assert.Equal("a", (await a).Text);
        Assert.Equal("b", (await b).Text);
        Assert.Equal("c", (await c).Text);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_removes_it_from_the_queue()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        var blocker = Submit(scheduler, Request(LocalTaskKind.UiAssist, "blocker"));
        var first = await backend.NextStartedAsync();
        using var cancelled = new CancellationTokenSource();
        var b = scheduler.SubmitAsync(Request(LocalTaskKind.VoiceCleanup, "b"), cancelled.Token);
        var a = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "a"));

        cancelled.Cancel();
        first.Complete("blocker");
        await blocker;

        var next = await backend.NextStartedAsync();
        Assert.Equal("a", next.Request.Input);
        next.Complete("a");
        Assert.Equal("a", (await a).Text);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        Assert.Equal(2, backend.CallCount);
    }

    [Fact]
    public async Task Cancelling_a_running_request_cancels_the_backend_call()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());
        using var cancelled = new CancellationTokenSource();

        var running = scheduler.SubmitAsync(Request(LocalTaskKind.VoiceCleanup, "running"), cancelled.Token);
        var call = await backend.NextStartedAsync();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(call.Token.IsCancellationRequested);

        var next = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "next"));
        var nextCall = await backend.NextStartedAsync();
        nextCall.Complete("next");
        Assert.Equal("next", (await next).Text);
    }

    [Fact]
    public async Task Cancel_background_stops_summaries_and_keeps_user_requests()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        var runningChip = Submit(scheduler, Request(LocalTaskKind.ChipSummary, "running-chip"));
        var running = await backend.NextStartedAsync();
        var queuedChip = Submit(scheduler, Request(LocalTaskKind.ChipSummary, "queued-chip"));
        var voice = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "voice"));

        scheduler.CancelBackground();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runningChip);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedChip);
        Assert.True(running.Token.IsCancellationRequested);

        var next = await backend.NextStartedAsync();
        Assert.Equal(LocalTaskKind.VoiceCleanup, next.Request.Kind);
        next.Complete("voice");
        Assert.Equal("voice", (await voice).Text);
    }

    [Fact]
    public async Task Identical_requests_share_one_execution()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        var first = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "same"));
        var second = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "same"));

        var call = await backend.NextStartedAsync();
        call.Complete("cleaned");

        Assert.Equal("cleaned", (await first).Text);
        Assert.Equal("cleaned", (await second).Text);
        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public async Task Cancelling_one_caller_keeps_the_shared_execution_for_the_others()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());
        using var leaving = new CancellationTokenSource();

        var gone = scheduler.SubmitAsync(Request(LocalTaskKind.VoiceCleanup, "same"), leaving.Token);
        var stays = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "same"));
        var call = await backend.NextStartedAsync();

        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gone);
        Assert.False(call.Token.IsCancellationRequested);

        call.Complete("cleaned");
        Assert.Equal("cleaned", (await stays).Text);
        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public async Task Cache_hit_returns_without_touching_the_backend()
    {
        var backend = new FakeLocalBackend().Reply("one");
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        var first = await Submit(scheduler, Request(LocalTaskKind.ChipSummary, "source-hash"));
        var second = await Submit(scheduler, Request(LocalTaskKind.ChipSummary, "source-hash"));

        Assert.Equal("one", first.Text);
        Assert.Equal(first, second);
        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public async Task Cache_is_keyed_by_model_version()
    {
        var cache = new InMemoryLocalResultCache();
        var v1Backend = new FakeLocalBackend().Reply("from v1");
        await using (var v1 = new LocalTaskScheduler(v1Backend, "v1", cache))
        {
            await Submit(v1, Request(LocalTaskKind.ChipSummary, "source-hash"));
        }

        var v2Backend = new FakeLocalBackend().Reply("from v2");
        await using var v2 = new LocalTaskScheduler(v2Backend, "v2", cache);
        var result = await Submit(v2, Request(LocalTaskKind.ChipSummary, "source-hash"));

        Assert.Equal("from v2", result.Text);
        Assert.Equal(1, v2Backend.CallCount);
    }

    [Fact]
    public async Task Failed_run_is_not_cached()
    {
        var backend = new FakeLocalBackend().Throw(new TimeoutException("slow")).Reply("ok");
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        await Assert.ThrowsAsync<TimeoutException>(() => Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "k")));
        var result = await Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "k"));

        Assert.Equal("ok", result.Text);
        Assert.Equal(2, backend.CallCount);
    }

    [Fact]
    public async Task Power_pause_holds_summaries_but_not_user_requests()
    {
        var power = new FakePowerState { IsBatterySaver = true };
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache(), power);

        var chip = Submit(scheduler, Request(LocalTaskKind.ChipSummary, "chip"));
        var voice = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "voice"));

        var call = await backend.NextStartedAsync();
        Assert.Equal(LocalTaskKind.VoiceCleanup, call.Request.Kind);
        call.Complete("voice");
        Assert.Equal("voice", (await voice).Text);
        Assert.False(chip.IsCompleted);
        Assert.Equal(1, backend.CallCount);

        power.IsBatterySaver = false;
        scheduler.NotifyPowerStateChanged();
        var resumed = await backend.NextStartedAsync();
        Assert.Equal(LocalTaskKind.ChipSummary, resumed.Request.Kind);
        resumed.Complete("chip");
        Assert.Equal("chip", (await chip).Text);
    }

    [Fact]
    public async Task Exception_is_shared_by_single_flight_callers()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());
        var error = new InvalidOperationException("blob mismatch");

        var first = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "same"));
        var second = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "same"));
        var call = await backend.NextStartedAsync();
        call.Fail(error);

        var firstError = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var secondError = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Same(error, firstError);
        Assert.Same(error, secondError);
        Assert.Equal(1, backend.CallCount);
    }

    [Fact]
    public async Task Runs_one_backend_call_at_a_time()
    {
        var backend = new GatedBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        var a = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "a"));
        var b = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "b"));

        var first = await backend.NextStartedAsync();
        first.Complete(first.Request.Input);
        var second = await backend.NextStartedAsync();
        second.Complete(second.Request.Input);

        await Task.WhenAll(a, b);
        Assert.Equal(1, backend.MaxActive);
    }

    [Fact]
    public async Task Request_cancelled_before_submit_never_reaches_the_backend()
    {
        var backend = new FakeLocalBackend();
        await using var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scheduler.SubmitAsync(Request(LocalTaskKind.VoiceCleanup, "k"), cancelled.Token));
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public async Task Dispose_cancels_queued_and_running_requests()
    {
        var backend = new GatedBackend();
        var scheduler = new LocalTaskScheduler(backend, "m1", new InMemoryLocalResultCache());

        var running = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "running"));
        var call = await backend.NextStartedAsync();
        var queued = Submit(scheduler, Request(LocalTaskKind.VoiceCleanup, "queued"));

        await scheduler.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(call.Token.IsCancellationRequested);
        Assert.Equal(1, backend.CallCount);
    }

    private sealed class FakePowerState : IPowerState
    {
        private volatile bool _batterySaver;

        public bool IsBatterySaver
        {
            get => _batterySaver;
            set => _batterySaver = value;
        }
    }
}
