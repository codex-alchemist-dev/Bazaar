using Bazaar.Server.Modules.Health;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Modules.Tests.Health;

/// <summary>Module health faults (doc 46 section 10 and 17.1): probes that block, return null or garbage, and garbage statuses.</summary>
public sealed class ModuleHealthChaosTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AProbeThatBlocksItsThreadStillTimesOut()
    {
        FakeTimeProvider time = new();
        using ManualResetEventSlim release = new();

        Task<ModuleHealthCheck> running = Task.Run(
            async () => await ModuleHealthProbeRunner.RunAsync(
                _ =>
                {
                    release.Wait(TimeSpan.FromSeconds(20), CancellationToken.None);
                    return ValueTask.FromResult(new ModuleHealthCheck(ModuleHealthStatus.Healthy, "ok", time.GetUtcNow()));
                },
                time,
                _timeout,
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        // Advance fake time until the runner's timer exists and fires; bounded, never waits on the wall clock.
        for (int spin = 0; spin < 100_000 && !running.IsCompleted; spin++)
        {
            time.Advance(_timeout + TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        ModuleHealthCheck result = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.Set();

        Assert.Equal(ModuleHealthStatus.Failing, result.Status);
    }

    [Fact]
    public async Task ANullResultIsFailingNotGreenAndNotACrash()
    {
        FakeTimeProvider time = new();

        ModuleHealthCheck result = await ModuleHealthProbeRunner.RunAsync(_ => ValueTask.FromResult<ModuleHealthCheck>(null!), time, _timeout, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModuleHealthStatus.Failing, result.Status);
    }

    [Fact]
    public void AnOutOfRangeStatusIsNeverCountedHealthyOrGreen()
    {
        DateTimeOffset at = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        ModuleHealthAggregate aggregate = ModuleHealthAggregator.Aggregate(
        [
            new ModuleHealthReport("a", [new ModuleHealthCheck((ModuleHealthStatus)999, "garbage", at), new ModuleHealthCheck(ModuleHealthStatus.Healthy, "ok", at)]),
        ]);

        Assert.Equal(ModuleHealthStatus.Unknown, aggregate.Status);
        Assert.Equal(ModuleHealthStatus.Unknown, aggregate.Modules[0].Status);
        Assert.NotEqual(ModuleHealthLight.Green, aggregate.Light);
        Assert.True(aggregate.Score <= ModuleHealthAggregator.UnknownScoreCap);
    }

    [Fact]
    public void ThousandsOfModulesAggregateWithTheDocumentedCaps()
    {
        DateTimeOffset at = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        List<ModuleHealthReport> modules = [.. Enumerable.Range(0, 5_000).Select(i =>
            new ModuleHealthReport("m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), [new ModuleHealthCheck(i == 4_999 ? ModuleHealthStatus.Failing : ModuleHealthStatus.Healthy, "x", at)]))];

        ModuleHealthAggregate aggregate = ModuleHealthAggregator.Aggregate(modules);

        Assert.Equal(ModuleHealthStatus.Failing, aggregate.Status);
        Assert.Equal(ModuleHealthLight.Red, aggregate.Light);
        Assert.True(aggregate.Score <= ModuleHealthAggregator.FailingScoreCap);
    }
}
