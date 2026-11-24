using Bazaar.Server.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Health.Tests;

public class HealthRetentionTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _today = new(2026, 11, 22);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<InMemoryHealthReportStore> SeedAsync()
    {
        var store = new InMemoryHealthReportStore();
        foreach (var age in new[] { 0, 29, 30, 31, 400 })
            await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _today.AddDays(-age), $"r{age}", "x"), Ct);
        return store;
    }

    [Fact]
    public async Task The_sweep_removes_reports_older_than_the_retention_and_keeps_the_boundary_day()
    {
        var store = await SeedAsync();
        var handler = new HealthRetentionJobHandler(store, new FixedRawRetentionSource(30), new FakeTimeProvider(_now));

        Assert.Equal(2, await handler.SweepAsync(Ct));

        Assert.Single(await store.ListAsync("p", _today.AddDays(-30), Ct));
        Assert.Empty(await store.ListAsync("p", _today.AddDays(-31), Ct));
        Assert.Empty(await store.ListAsync("p", _today.AddDays(-400), Ct));
        Assert.Equal(0, await handler.SweepAsync(Ct));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100000, 3650)]
    public async Task An_out_of_range_retention_is_clamped(int configured, int effective)
    {
        var store = new InMemoryHealthReportStore();
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _today.AddDays(-effective), "edge", "x"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _today.AddDays(-effective - 1), "past", "x"), Ct);
        var handler = new HealthRetentionJobHandler(store, new FixedRawRetentionSource(configured), new FakeTimeProvider(_now));

        Assert.Equal(1, await handler.SweepAsync(Ct));
        Assert.Single(await store.ListAsync("p", _today.AddDays(-effective), Ct));
    }

    [Fact]
    public async Task The_component_registers_the_sweep_with_a_daily_cadence_and_a_host_can_replace_the_retention()
    {
        var services = new ServiceCollection();
        services.AddBazaarHealth();
        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<IJobHandler>(), h => h.Kind == HealthRetentionJobHandler.JobKind);
        Assert.Contains(provider.GetServices<RecurringJob>(), j => j.Kind == HealthRetentionJobHandler.JobKind && j.Interval == TimeSpan.FromDays(1));
        Assert.Equal(90, await provider.GetRequiredService<IRawRetentionSource>().GetDaysAsync(Ct));

        services.AddSingleton<IRawRetentionSource>(new FixedRawRetentionSource(7));
        using var replaced = services.BuildServiceProvider();
        Assert.Equal(7, await replaced.GetRequiredService<IRawRetentionSource>().GetDaysAsync(Ct));
    }
}
