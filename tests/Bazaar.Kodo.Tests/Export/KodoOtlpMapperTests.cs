using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Export;

public sealed class KodoOtlpMapperTests
{
    [Fact]
    public void GaugeNamesAreTheDottedCatalogueNames()
    {
        IReadOnlyList<KodoOtlpGauge> gauges = KodoOtlpMapper.Map(KodoExportTestData.Snapshot(Bazaar.Kodo.Evaluator.KodoState.Healthy));

        Assert.Equal(KodoMetricNames.All, gauges.Select(gauge => gauge.Name));
    }

    [Fact]
    public void EveryPointCarriesTheSnapshotTimeInUnixNanoseconds()
    {
        IReadOnlyList<KodoOtlpGauge> gauges = KodoOtlpMapper.Map(KodoExportTestData.Snapshot(Bazaar.Kodo.Evaluator.KodoState.Healthy));

        long expected = KodoExportTestData.Now.ToUnixTimeMilliseconds() * 1_000_000;
        Assert.All(gauges.SelectMany(gauge => gauge.DataPoints), point => Assert.Equal(expected, point.TimeUnixNano));
    }

    [Fact]
    public void ProbeAttributesUseOpenTelemetryKeys()
    {
        IReadOnlyList<KodoOtlpGauge> gauges = KodoOtlpMapper.Map(
            KodoExportTestData.Snapshot(
                Bazaar.Kodo.Evaluator.KodoState.Degraded,
                components: KodoExportTestData.Component("host.disk.free", ProbeStatus.Amber)));

        KodoOtlpGauge probes = gauges.Single(gauge => gauge.Name == KodoMetricNames.ProbeStatus);
        KodoOtlpDataPoint point = Assert.Single(probes.DataPoints);

        Assert.Equal(1d, point.AsDouble);
        Assert.Equal([("probe.id", "host.disk.free"), ("module.id", KodoAssembly.ModuleId)], point.Attributes.Select(a => (a.Key, a.Value)));
    }

    [Fact]
    public void StateAttributeIsKeyedStateAndOnlyOneSeriesIsActive()
    {
        IReadOnlyList<KodoOtlpGauge> gauges = KodoOtlpMapper.Map(KodoExportTestData.Snapshot(Bazaar.Kodo.Evaluator.KodoState.Recovering));

        KodoOtlpGauge state = gauges.Single(gauge => gauge.Name == KodoMetricNames.State);

        Assert.All(state.DataPoints, point => Assert.Equal("state", Assert.Single(point.Attributes).Key));
        Assert.Single(state.DataPoints, point => point.AsDouble == 1);
        Assert.Equal("recovering", state.DataPoints.Single(point => point.AsDouble == 1).Attributes[0].Value);
    }

    [Fact]
    public void AttributeKeysAreRefusedOutsideTheAllowList()
    {
        Assert.Throws<ArgumentException>(() => KodoOtlpMapper.AttributeKey("agent_id"));
        Assert.Throws<ArgumentException>(() => KodoOtlpMapper.AttributeKey("gc_id"));
    }

    [Fact]
    public void ScoreHasNoAttributes()
    {
        IReadOnlyList<KodoOtlpGauge> gauges = KodoOtlpMapper.Map(KodoExportTestData.Snapshot(Bazaar.Kodo.Evaluator.KodoState.Healthy, score: 77));

        KodoOtlpDataPoint point = Assert.Single(gauges.Single(gauge => gauge.Name == KodoMetricNames.Score).DataPoints);

        Assert.Empty(point.Attributes);
        Assert.Equal(77d, point.AsDouble);
    }
}
