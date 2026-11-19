using Bazaar.Server.Modules.Health;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Modules.Tests.Health;

/// <summary>Probe timeouts and exception handling (doc 46 section 10.3, rule 4 and doc 42 section 11). Uses a fake clock.</summary>
public sealed class ModuleHealthProbeRunnerTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    // Passes the test host's token, as the xUnit analyzer asks, so a hung probe test is cancelled with the run.
    private static ValueTask<ModuleHealthCheck> Run(
        Func<CancellationToken, ValueTask<ModuleHealthCheck>> probe,
        TimeProvider clock,
        TimeSpan? timeout = null) =>
        ModuleHealthProbeRunner.RunAsync(probe, clock, timeout, TestContext.Current.CancellationToken);

    private static ValueTask<ModuleHealthCheck> Healthy(CancellationToken _) =>
        ValueTask.FromResult(new ModuleHealthCheck(ModuleHealthStatus.Healthy, "ok", _start));

    // Ignores its token on purpose: the runner must still give up on time.
    private static ValueTask<ModuleHealthCheck> IgnoresCancellation(CancellationToken _) =>
        new(new TaskCompletionSource<ModuleHealthCheck>().Task);

    // Honours its token, so a timed-out probe stops as soon as the runner cancels it.
    private static async ValueTask<ModuleHealthCheck> HonoursCancellation(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new ModuleHealthCheck(ModuleHealthStatus.Healthy, "unreachable", _start);
    }

    [Fact]
    public async Task Returns_the_probe_result_when_it_finishes_in_time()
    {
        var clock = new FakeTimeProvider(_start);

        var result = await Run(Healthy, clock, TimeSpan.FromSeconds(5));

        Assert.Equal(ModuleHealthStatus.Healthy, result.Status);
        Assert.Equal("ok", result.Reason);
    }

    [Fact]
    public async Task Timeout_becomes_failing_with_the_probe_timed_out_reason()
    {
        var clock = new FakeTimeProvider(_start);
        var task = Run(IgnoresCancellation, clock, TimeSpan.FromSeconds(5)).AsTask();

        clock.Advance(TimeSpan.FromSeconds(5));
        var result = await task;

        Assert.Equal(ModuleHealthStatus.Failing, result.Status);
        Assert.Equal("probe timed out", result.Reason);
        Assert.Equal(_start.AddSeconds(5), result.CheckedAt);
    }

    [Fact]
    public async Task Timeout_stops_a_probe_that_honours_cancellation()
    {
        var clock = new FakeTimeProvider(_start);
        var task = Run(HonoursCancellation, clock, TimeSpan.FromSeconds(2)).AsTask();

        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await task;

        Assert.Equal(ModuleHealthStatus.Failing, result.Status);
        Assert.Equal("probe timed out", result.Reason);
    }

    [Fact]
    public async Task Probe_finishing_just_before_the_timeout_keeps_its_own_result()
    {
        var clock = new FakeTimeProvider(_start);
        var completion = new TaskCompletionSource<ModuleHealthCheck>();
        var task = Run(_ => new ValueTask<ModuleHealthCheck>(completion.Task), clock, TimeSpan.FromSeconds(5)).AsTask();

        completion.SetResult(new ModuleHealthCheck(ModuleHealthStatus.Degraded, "slow disk", _start));
        var result = await task;

        Assert.Equal(ModuleHealthStatus.Degraded, result.Status);
        Assert.Equal("slow disk", result.Reason);
    }

    [Fact]
    public async Task Exception_becomes_failing_with_the_type_name_and_no_message()
    {
        var clock = new FakeTimeProvider(_start);

        var result = await Run(
            _ => throw new InvalidOperationException("password=hunter2 at C:\\secrets"),
            clock,
            TimeSpan.FromSeconds(5));

        Assert.Equal(ModuleHealthStatus.Failing, result.Status);
        Assert.Equal("probe threw InvalidOperationException", result.Reason);
        Assert.DoesNotContain("hunter2", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Async_exception_is_converted_the_same_way()
    {
        var clock = new FakeTimeProvider(_start);

        static async ValueTask<ModuleHealthCheck> Throws(CancellationToken _)
        {
            await Task.Yield();
            throw new TimeoutException("socket 10.0.0.5");
        }

        var result = await Run(Throws, clock, TimeSpan.FromSeconds(5));

        Assert.Equal("probe threw TimeoutException", result.Reason);
        Assert.DoesNotContain("10.0.0.5", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_turned_into_a_health_result()
    {
        var clock = new FakeTimeProvider(_start);
        using var cancellation = new CancellationTokenSource();
        var task = ModuleHealthProbeRunner.RunAsync(HonoursCancellation, clock, TimeSpan.FromSeconds(5), cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Already_cancelled_caller_token_throws_before_the_probe_runs()
    {
        var clock = new FakeTimeProvider(_start);
        var probeRan = false;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ModuleHealthProbeRunner.RunAsync(
                _ =>
                {
                    probeRan = true;
                    return Healthy(default);
                },
                clock,
                TimeSpan.FromSeconds(5),
                cancellation.Token));

        Assert.False(probeRan);
    }

    [Fact]
    public async Task Default_timeout_is_five_seconds()
    {
        var clock = new FakeTimeProvider(_start);
        var task = Run(IgnoresCancellation, clock).AsTask();

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(task.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await task;

        Assert.Equal(TimeSpan.FromSeconds(5), ModuleHealthProbeRunner.DefaultTimeout);
        Assert.Equal("probe timed out", result.Reason);
    }

    [Fact]
    public async Task Zero_or_negative_timeout_is_rejected()
    {
        var clock = new FakeTimeProvider(_start);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Run(Healthy, clock, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Run(Healthy, clock, TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task Probe_that_throws_inside_a_contract_is_failing_not_healthy()
    {
        var clock = new FakeTimeProvider(_start);
        var module = new FakeModuleHealthContract("kodo")
        {
            ReadinessProbe = _ => throw new InvalidOperationException("boom"),
        };

        var result = await Run(module.ReadinessAsync, clock, TimeSpan.FromSeconds(1));

        Assert.Equal(ModuleHealthStatus.Failing, result.Status);
        Assert.Equal("probe threw InvalidOperationException", result.Reason);
    }
}
