using Bazaar.Contracts.Kodo;
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
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing),
            new KodoObservation(),
            score: 42,
            components: [],
            revision: 7,
            generatedAt: _now,
            journalHead: KodoApiTestData.JournalHead());

        Assert.Equal(KodoStatusSnapshotDto.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(7, snapshot.Revision);
        Assert.Equal(_now, snapshot.Status.AsOf);
        Assert.Equal("frozen", snapshot.Status.State);
        Assert.Equal("failing", snapshot.Status.UnderlyingState);
        Assert.Equal(42, snapshot.Status.Score);
        Assert.Empty(snapshot.Status.Controls);
        Assert.Equal(KodoApiTestData.JournalHeadHex, snapshot.JournalHead);
        Assert.Empty(snapshot.Components);
    }

    [Fact]
    public void JournalHeadIsNullWhileTheJournalIsEmpty()
    {
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), 100, [], 0, _now, journalHead: null);

        Assert.Null(snapshot.JournalHead);
    }

    [Fact]
    public void ComponentsAreSortedByIdWithOrdinalOrder()
    {
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy),
            new KodoObservation(),
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
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Degraded),
            new KodoObservation(),
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
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy),
            new KodoObservation(),
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
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Degraded),
            new KodoObservation(),
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
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy),
            new KodoObservation(),
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

        KodoStatusSnapshotDto first = KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Failing), new KodoObservation(), 30, components, 2, _now, null);
        KodoStatusSnapshotDto second = KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Failing), new KodoObservation(), 30, components, 2, _now, null);

        // Records with a collection compare by reference, so the determinism check compares the encoded bytes.
        Assert.Equal(KodoWireCodec.EncodeSnapshot(first), KodoWireCodec.EncodeSnapshot(second));
    }

    [Fact]
    public void RejectsAScoreOutsideZeroToHundred()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), -1, [], 0, _now, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), 101, [], 0, _now, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void AcceptsTheScoreBoundaries(int score)
    {
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), score, [], 0, _now, null);

        Assert.Equal(score, snapshot.Status.Score);
    }

    [Fact]
    public void RejectsANegativeRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), 100, [], -1, _now, null));
    }

    [Fact]
    public void RejectsAJournalHeadThatIsNotAFullHash()
    {
        Assert.Throws<ArgumentException>(() =>
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), 100, [], 0, _now, new byte[31]));
    }

    [Fact]
    public void RejectsADuplicateComponentId()
    {
        Assert.Throws<ArgumentException>(() =>
            KodoStatusSnapshotBuilder.Build(
                KodoApiTestData.Evaluation(KodoState.Healthy),
                new KodoObservation(),
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
                KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), 100, [KodoApiTestData.Green(id)], 0, _now, null));
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
                new KodoObservation(),
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
                new KodoObservation(),
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
            KodoStatusSnapshotBuilder.Build(KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), 100, [null!], 0, _now, null));
    }

    [Fact]
    public void UnderlyingStateIsOmittedUnlessFrozen()
    {
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Degraded), new KodoObservation(), 80, [], 0, _now, null);

        Assert.Equal("degraded", snapshot.Status.State);
        Assert.Null(snapshot.Status.UnderlyingState);
    }

    [Fact]
    public void ControlsAreCarriedInTheStatusPayload()
    {
        KodoControlDto[] controls = [new KodoControlDto("restart", false, "Incident mode is active")];

        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing),
            new KodoObservation(),
            40,
            [],
            0,
            _now,
            null,
            controls);

        Assert.Equal(controls, snapshot.Status.Controls);
    }

    [Theory]
    [InlineData(true, false, false, 49)]
    [InlineData(false, true, false, 10)]
    [InlineData(false, false, true, 70)]
    [InlineData(true, true, false, 10)]
    [InlineData(true, false, true, 49)]
    [InlineData(true, true, true, 10)]
    public void ScoreAtTheCapIsAcceptedAndOneAboveIsRejected(bool criticalRed, bool integrity, bool criticalUnknown, int cap)
    {
        KodoObservation observation = new()
        {
            CriticalRed = criticalRed,
            IntegrityOrContractFailure = integrity,
            CriticalUnknown = criticalUnknown,
        };
        KodoEvaluation evaluation = KodoApiTestData.Evaluation(KodoState.Degraded);

        KodoStatusSnapshotDto accepted = KodoStatusSnapshotBuilder.Build(evaluation, observation, cap, [], 0, _now, null);
        Assert.Equal(cap, accepted.Status.Score);

        Assert.Throws<ArgumentException>(() =>
            KodoStatusSnapshotBuilder.Build(evaluation, observation, cap + 1, [], 0, _now, null));
    }

    [Fact]
    public void FailingEvaluationWithAScoreAboveFortyNineIsRejected()
    {
        KodoObservation clean = new();

        Assert.Throws<ArgumentException>(() => KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Failing), clean, 50, [], 0, _now, null));
        Assert.Throws<ArgumentException>(() => KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing), clean, 100, [], 0, _now, null));
        Assert.Equal(49, KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Failing), clean, 49, [], 0, _now, null).Status.Score);
    }

    [Fact]
    public void ScoreCapsMatchTheScoreComputation()
    {
        // The builder and KodoScore.Compute share one cap, so a computed score is always accepted.
        KodoObservation observation = new() { CriticalRed = true };
        Dictionary<KodoCategory, int> perfect = Enum.GetValues<KodoCategory>().ToDictionary(category => category, _ => 100);

        int score = KodoScore.Compute(perfect, observation);

        Assert.Equal(KodoScore.CapFor(observation), score);
        Assert.Equal(score, KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Failing), observation, score, [], 0, _now, null).Status.Score);
    }
}
