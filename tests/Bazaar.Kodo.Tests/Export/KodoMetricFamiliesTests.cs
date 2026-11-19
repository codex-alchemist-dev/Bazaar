using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Export;

public sealed class KodoMetricFamiliesTests
{
    [Fact]
    public void StateIsOneHotOverTheFiveDisplayedStates()
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(KodoState.Degraded, KodoState.Degraded, 80);

        KodoMetricFamily state = Family(snapshot, KodoMetricNames.State);

        Assert.Equal(5, state.Points.Count);
        Assert.Equal(["healthy", "degraded", "failing", "recovering", "frozen"], state.Points.Select(LabelValue));
        Assert.Equal([0d, 1d, 0d, 0d, 0d], state.Points.Select(point => point.Value));
    }

    [Fact]
    public void FrozenShowsTheUnderlyingStateBesideIt()
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(KodoState.Frozen, KodoState.Failing, 40);

        KodoMetricFamily underlying = Family(snapshot, KodoMetricNames.UnderlyingState);

        Assert.Equal(["healthy", "degraded", "failing", "recovering"], underlying.Points.Select(LabelValue));
        Assert.Equal([0d, 0d, 1d, 0d], underlying.Points.Select(point => point.Value));
        Assert.Equal(1d, Family(snapshot, KodoMetricNames.State).Points.Single(point => LabelValue(point) == "frozen").Value);
    }

    [Fact]
    public void ScoreIsOneUnlabelledPoint()
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(KodoState.Healthy, score: 93);

        KodoMetricFamily score = Family(snapshot, KodoMetricNames.Score);

        KodoMetricPoint point = Assert.Single(score.Points);
        Assert.Equal(93d, point.Value);
        Assert.Empty(point.Labels);
    }

    [Theory]
    [InlineData(ProbeStatus.Green, 0)]
    [InlineData(ProbeStatus.Amber, 1)]
    [InlineData(ProbeStatus.Red, 2)]
    [InlineData(ProbeStatus.Unknown, 3)]
    public void ProbeStatusSeverityOrdersUnknownAboveRed(ProbeStatus status, double expected)
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(
            KodoState.Healthy,
            components: KodoExportTestData.Component("host.disk.free", status));

        KodoMetricPoint point = Assert.Single(Family(snapshot, KodoMetricNames.ProbeStatus).Points);

        Assert.Equal(expected, point.Value);
    }

    [Fact]
    public void ProbePointsCarryOnlyProbeIdAndModuleId()
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(
            KodoState.Healthy,
            components:
            [
                KodoExportTestData.Component("host.disk.free", ProbeStatus.Green),
                KodoExportTestData.Component("db.postgres.ping", ProbeStatus.Red),
            ]);

        // The snapshot sorts components by id, so db.postgres.ping comes before host.disk.free.
        IReadOnlyList<KodoMetricLabel> labels = Family(snapshot, KodoMetricNames.ProbeStatus).Points[0].Labels;

        Assert.Equal([KodoMetricLabels.ProbeId, KodoMetricLabels.ModuleId], labels.Select(label => label.Name));
        Assert.Equal(["db.postgres.ping", KodoAssembly.ModuleId], labels.Select(label => label.Value));
    }

    [Fact]
    public void EveryLabelNameIsOnTheAllowList()
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(
            KodoState.Failing,
            KodoState.Failing,
            20,
            KodoExportTestData.Component("host.memory.pressure", ProbeStatus.Amber));

        IEnumerable<string> names = KodoMetricFamilies.From(snapshot)
            .SelectMany(family => family.Points)
            .SelectMany(point => point.Labels)
            .Select(label => label.Name)
            .Distinct();

        Assert.All(names, name => Assert.Contains(name, KodoMetricLabels.Allowed));
        Assert.DoesNotContain("agent_id", names);
        Assert.DoesNotContain("gc_id", names);
    }

    [Theory]
    [InlineData("agent.5f2c9a1b7d3e4f60")]
    [InlineData("gc.0123456789abcdef")]
    [InlineData("Host.Disk.Free")]
    [InlineData("host disk free")]
    [InlineData("host.disk-free")]
    public void RefusesIdsThatAreNotProbeIdsOrThatLookLikeInstanceIds(string id)
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(
            KodoState.Healthy,
            components: KodoExportTestData.Component(id, ProbeStatus.Green));

        Assert.Throws<ArgumentException>(() => { _ = KodoMetricFamilies.From(snapshot); });
    }

    [Fact]
    public void RefusesAModuleIdThatIsNotAModuleId()
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(
            KodoState.Healthy,
            components: KodoExportTestData.Component("host.disk.free", ProbeStatus.Green));

        Assert.Throws<ArgumentException>(() => { _ = KodoMetricFamilies.From(snapshot, _ => "Kodo Module"); });
    }

    [Fact]
    public void ModuleIdComesFromTheLookup()
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(
            KodoState.Healthy,
            components: KodoExportTestData.Component("host.disk.free", ProbeStatus.Green));

        KodoMetricFamily family = Family(snapshot, KodoMetricNames.ProbeStatus, _ => "host");

        Assert.Equal("host", Assert.Single(family.Points).Labels[1].Value);
    }

    [Fact]
    public void RefusesMoreProbeSeriesThanTheCap()
    {
        KodoComponentInput[] components = new KodoComponentInput[KodoMetricFamilies.MaxProbeSeries + 1];
        for (int i = 0; i < components.Length; i++)
        {
            components[i] = KodoExportTestData.Component($"host.probe{i}", ProbeStatus.Green);
        }

        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(KodoState.Healthy, components: components);

        Assert.Throws<InvalidOperationException>(() => { _ = KodoMetricFamilies.From(snapshot); });
    }

    [Fact]
    public void AcceptsExactlyTheCap()
    {
        KodoComponentInput[] components = new KodoComponentInput[KodoMetricFamilies.MaxProbeSeries];
        for (int i = 0; i < components.Length; i++)
        {
            components[i] = KodoExportTestData.Component($"host.probe{i}", ProbeStatus.Green);
        }

        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(KodoState.Healthy, components: components);

        Assert.Equal(components.Length, Family(snapshot, KodoMetricNames.ProbeStatus).Points.Count);
    }

    [Fact]
    public void RefusesAStateWordThatIsNotAState()
    {
        KodoStatusSnapshot snapshot = new(1, 1, KodoExportTestData.Now, "bogus", "healthy", 100, null, []);

        Assert.Throws<ArgumentException>(() => { _ = KodoMetricFamilies.From(snapshot); });
    }

    [Fact]
    public void FamilyNamesAreTheCataloguedNames()
    {
        KodoStatusSnapshot snapshot = KodoExportTestData.Snapshot(KodoState.Healthy);

        Assert.Equal(KodoMetricNames.All, KodoMetricFamilies.From(snapshot).Select(family => family.Name));
    }

    private static KodoMetricFamily Family(KodoStatusSnapshot snapshot, string name, Func<string, string>? moduleIdOf = null) =>
        KodoMetricFamilies.From(snapshot, moduleIdOf).Single(family => family.Name == name);

    private static string LabelValue(KodoMetricPoint point) => point.Labels.Single().Value;
}
