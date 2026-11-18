using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Api;

public sealed class KodoStatusSnapshotBuilderTests
{
    private static readonly DateTimeOffset _now = KodoApiTestData.Now;

    [Fact]
    public void BuildCopiesTheEvaluatorStatesAndTheScoreAndRevision()
    {
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing),
            score: 42,
            components: [],
            revision: 7,
            generatedAt: _now,
            journalHead: KodoApiTestData.JournalHead());

        Assert.Equal(KodoStatusSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(7, snapshot.Revision);
        Assert.Equal(_now, snapshot.GeneratedAt);
        Assert.Equal("frozen", snapshot.State);
        Assert.Equal("failing", snapshot.Underlying);
        Assert.Equal(42, snapshot.Score);
        Assert.Equal(KodoApiTestData.JournalHeadHex, snapshot.JournalHead);
        Assert.Empty(snapshot.Components);
    }

    [Fact]
    public void JournalHeadIsNullWhileTheJournalIsEmpty()
    {
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy), 100, [], 0, _now, journalHead: null);

        Assert.Null(snapshot.JournalHead);
    }

    [Fact]
    public void ComponentsAreSortedByIdWithOrdinalOrder()
    {
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy),
            100,
            [
                KodoApiTestData.Green("host.disk.free"),
                KodoApiTestData.Green("Zeta"),
                KodoApiTestData.Green("db.backup_freshness"),
            ],
            1,
            _now,
            null);

        Assert.Equal(["Zeta", "db.backup_freshness", "host.disk.free"], snapshot.Components.Select(component => component.Id));
    }

    [Fact]
    public void ComponentStatusesUseTheWireStrings()
    {
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Degraded),
            80,
            [
                new KodoComponentInput("a.green", ProbeStatus.Green, null, _now),
                new KodoComponentInput("b.amber", ProbeStatus.Amber, "disk_usage_warn", _now),
                new KodoComponentInput("c.red", ProbeStatus.Red, "timeout", _now),
                new KodoComponentInput("d.unknown", ProbeStatus.Unknown, "breaker_open", _now),
            ],
            3,
            _now,
            null);

        Assert.Equal(
            ["green", "amber", "red", "unknown"],
            snapshot.Components.Select(component => component.State));
        Assert.Equal("disk_usage_warn", snapshot.Components[1].Cause);
        Assert.Equal("breaker_open", snapshot.Components[3].Cause);
    }

    [Fact]
    public void GreenComponentDropsAnyCauseSoOneGreenHasOneShape()
    {
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy),
            100,
            [new KodoComponentInput("host.clock.drift", ProbeStatus.Green, "clock_drift_warn", _now)],
            1,
            _now,
            null);

        Assert.Null(snapshot.Components[0].Cause);
    }

    [Fact]
    public void ComponentKeepsItsSinceTime()
    {
        DateTimeOffset since = _now.AddMinutes(-5);
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Degraded),
            80,
            [new KodoComponentInput("host.disk.free", ProbeStatus.Amber, "disk_usage_warn", since)],
            1,
            _now,
            null);

        Assert.Equal(since, snapshot.Components[0].Since);
    }

    [Fact]
    public void ComponentSinceAtTheGenerationTimeIsAllowed()
    {
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy),
            100,
            [KodoApiTestData.Green("host.disk.free")],
            1,
            _now,
            null);

        Assert.Single(snapshot.Components);
    }

    [Fact]
    public void BuildIsDeterministicForTheSameInputs()
    {
        KodoComponentInput[] components =
        [
            new("b", ProbeStatus.Red, "timeout", _now),
            new("a", ProbeStatus.Green, null, _now),
        ];

        KodoStatusSnapshot first = KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Failing), 30, components, 2, _now, null);
        KodoStatusSnapshot second = KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Failing), 30, components, 2, _now, null);

        // Records with a collection compare by reference, so the determinism check compares the encoded bytes.
        Assert.Equal(KodoApiCodec.EncodeSnapshot(first), KodoApiCodec.EncodeSnapshot(second));
    }

    [Fact]
    public void RejectsAScoreOutsideZeroToHundred()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), -1, [], 0, _now, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), 101, [], 0, _now, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void AcceptsTheScoreBoundaries(int score)
    {
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), score, [], 0, _now, null);

        Assert.Equal(score, snapshot.Score);
    }

    [Fact]
    public void RejectsANegativeRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), 100, [], -1, _now, null));
    }

    [Fact]
    public void RejectsAJournalHeadThatIsNotAFullHash()
    {
        Assert.Throws<ArgumentException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), 100, [], 0, _now, new byte[31]));
    }

    [Fact]
    public void RejectsADuplicateComponentId()
    {
        Assert.Throws<ArgumentException>(() =>
            KodoStatusSnapshotBuilder.Build(
                KodoApiTestData.Evaluation(KodoState.Healthy),
                100,
                [KodoApiTestData.Green("host.disk.free"), KodoApiTestData.Green("host.disk.free")],
                0,
                _now,
                null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsABlankComponentId(string id)
    {
        Assert.Throws<ArgumentException>(() =>
            KodoStatusSnapshotBuilder.Build(
                KodoApiTestData.Evaluation(KodoState.Healthy), 100, [KodoApiTestData.Green(id)], 0, _now, null));
    }

    [Theory]
    [InlineData(ProbeStatus.Amber)]
    [InlineData(ProbeStatus.Red)]
    [InlineData(ProbeStatus.Unknown)]
    public void RejectsANonGreenComponentWithoutACause(ProbeStatus status)
    {
        Assert.Throws<ArgumentException>(() =>
            KodoStatusSnapshotBuilder.Build(
                KodoApiTestData.Evaluation(KodoState.Degraded),
                80,
                [new KodoComponentInput("host.disk.free", status, null, _now)],
                0,
                _now,
                null));
    }

    [Fact]
    public void RejectsAComponentThatEnteredItsStateAfterTheSnapshotWasGenerated()
    {
        Assert.Throws<ArgumentException>(() =>
            KodoStatusSnapshotBuilder.Build(
                KodoApiTestData.Evaluation(KodoState.Healthy),
                100,
                [new KodoComponentInput("host.disk.free", ProbeStatus.Green, null, _now.AddSeconds(1))],
                0,
                _now,
                null));
    }

    [Fact]
    public void RejectsANullComponentEntry()
    {
        Assert.Throws<ArgumentNullException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), 100, [null!], 0, _now, null));
    }
}
