using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Probes;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Setup.Probes;

public sealed class ProbeSetTests
{
    private sealed class OwnProbe(string id) : ISetupInfrastructureProbe
    {
        public string CheckId { get; } = id;

        public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SetupProbeReading(SetupProbeOutcome.Healthy, "own"));
    }

    private sealed class FakeLock : IInstanceLockReader
    {
        public bool IsHeld => true;
    }

    [Fact]
    public void Without_a_database_or_host_seams_only_the_self_contained_probes_are_built()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var ids = SetupVerificationProbeSet.Build(provider).Select(probe => probe.CheckId).ToList();

        Assert.Contains("host.os", ids);
        Assert.Contains("host.disk", ids);
        Assert.Contains("models.reachable", ids);
        Assert.DoesNotContain("database.postgres", ids);
        Assert.DoesNotContain("schema.migrations", ids);
        Assert.DoesNotContain("instance.lock", ids);
        Assert.DoesNotContain("server.identity", ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_registered_seam_adds_its_probe_and_a_registered_probe_wins_over_the_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInstanceLockReader>(new FakeLock());
        services.AddSingleton<ISetupInfrastructureProbe>(new OwnProbe("host.os"));
        using var provider = services.BuildServiceProvider();

        var probes = SetupVerificationProbeSet.Build(provider);

        Assert.Contains(probes, probe => probe.CheckId == "instance.lock");
        Assert.IsType<OwnProbe>(Assert.Single(probes, probe => probe.CheckId == "host.os"));
    }

    [Fact]
    public async Task Every_probe_the_set_builds_answers_a_check_id_the_catalogue_lists()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var ids = SetupVerificationChecks.Infrastructure.Select(check => check.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var probe in SetupVerificationProbeSet.Build(provider))
        {
            Assert.Contains(probe.CheckId, ids);
        }

        // End to end through the check list: a built probe turns its placeholder into a real row.
        var checks = SetupVerificationChecks.Create(SetupVerificationProbeSet.Build(provider));
        var os = await checks.Single(check => check.Id == "host.os").RunAsync(new SetupVerificationContext(new SetupPlan()), TestContext.Current.CancellationToken);
        Assert.NotEqual(SetupCheckStatus.NotEvaluated, os.Status);
    }
}
