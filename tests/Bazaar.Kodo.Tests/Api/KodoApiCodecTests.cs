using System.Text;
using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Api;

public sealed class KodoApiCodecTests
{
    private static readonly DateTimeOffset _now = KodoApiTestData.Now;

    [Fact]
    public void EmptyHealthySnapshotHasAFixedShapeWithEveryFieldWritten()
    {
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy), new KodoObservation(), 100, [], 0, _now, null);

        string json = Encoding.UTF8.GetString(KodoWireCodec.EncodeSnapshot(snapshot));

        Assert.Equal(
            "{\"schemaVersion\":1,\"revision\":0,\"status\":{\"state\":\"healthy\",\"score\":100,"
            + "\"asOf\":\"2026-11-18T12:00:00+00:00\",\"controls\":[]},\"components\":[]}",
            json);
    }

    [Fact]
    public void FrozenSnapshotWithComponentsAndJournalHeadHasAStableEncoding()
    {
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing),
            new KodoObservation(),
            score: 49,
            components:
            [
                new KodoComponentInput("db.backup_freshness", ProbeStatus.Red, "timeout", _now.AddMinutes(-2)),
                new KodoComponentInput("host.disk.free", ProbeStatus.Green, null, _now),
            ],
            revision: 12,
            generatedAt: _now,
            journalHead: KodoApiTestData.JournalHead());

        string json = Encoding.UTF8.GetString(KodoWireCodec.EncodeSnapshot(snapshot));

        Assert.Equal(
            "{\"schemaVersion\":1,\"revision\":12,\"status\":{\"state\":\"frozen\",\"underlyingState\":\"failing\",\"score\":49,"
            + "\"asOf\":\"2026-11-18T12:00:00+00:00\",\"controls\":[]},"
            + "\"journalHead\":\"" + KodoApiTestData.JournalHeadHex + "\","
            + "\"components\":["
            + "{\"id\":\"db.backup_freshness\",\"state\":\"red\",\"cause\":\"timeout\",\"since\":\"2026-11-18T11:58:00+00:00\"},"
            + "{\"id\":\"host.disk.free\",\"state\":\"green\",\"since\":\"2026-11-18T12:00:00+00:00\"}]}",
            json);
    }

    [Fact]
    public void EncodingIsDeterministicForTheSameSnapshot()
    {
        KodoStatusSnapshotDto snapshot = KodoApiTestData.Snapshot(3);

        Assert.Equal(KodoWireCodec.EncodeSnapshot(snapshot), KodoWireCodec.EncodeSnapshot(snapshot));
    }

    [Fact]
    public void StateChangedEventHasAFixedShape()
    {
        KodoStreamWriter writer = new();
        KodoStreamEventDto streamEvent = writer.StateChanged(
            KodoState.Healthy, KodoApiTestData.Evaluation(KodoState.Degraded), _now);

        string json = Encoding.UTF8.GetString(KodoWireCodec.EncodeEvent(streamEvent));

        Assert.Equal(
            "{\"revision\":1,\"type\":\"state_changed\",\"at\":\"2026-11-18T12:00:00+00:00\","
            + "\"stateChange\":{\"previous\":\"healthy\",\"state\":\"degraded\"}}",
            json);
    }

    [Fact]
    public void HeartbeatEventHasNoPayload()
    {
        KodoStreamWriter writer = new();

        string json = Encoding.UTF8.GetString(KodoWireCodec.EncodeEvent(writer.Heartbeat(_now)));

        Assert.Equal(
            "{\"revision\":1,\"type\":\"heartbeat\",\"at\":\"2026-11-18T12:00:00+00:00\"}",
            json);
    }

    [Fact]
    public void SnapshotEventCarriesTheSnapshotAndTheCurrentRevision()
    {
        KodoStreamWriter writer = new(lastRevision: 4);

        string json = Encoding.UTF8.GetString(KodoWireCodec.EncodeEvent(
            writer.Snapshot(revision => KodoApiTestData.Snapshot(revision), _now)));

        Assert.StartsWith(
            "{\"revision\":4,\"type\":\"snapshot\",\"at\":\"2026-11-18T12:00:00+00:00\",\"snapshot\":{\"schemaVersion\":1,\"revision\":4,",
            json);
    }

    [Fact]
    public void EncodedSnapshotRoundTrips()
    {
        KodoStatusSnapshotDto snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing),
            new KodoObservation(),
            49,
            [new KodoComponentInput("db.backup_freshness", ProbeStatus.Red, "timeout", _now)],
            12,
            _now,
            KodoApiTestData.JournalHead());

        KodoStatusSnapshotDto? back = KodoWireCodec.DecodeSnapshot(KodoWireCodec.EncodeSnapshot(snapshot));

        Assert.NotNull(back);
        Assert.Equal(snapshot.Status, back.Status with { Controls = snapshot.Status.Controls });
        Assert.Equal(snapshot.Revision, back.Revision);
        Assert.Equal(snapshot.JournalHead, back.JournalHead);
        Assert.Equal(snapshot.Components, back.Components);
    }

    [Fact]
    public void WireStringsForStatesAndDisplayValuesAreStable()
    {
        Assert.Equal("healthy", KodoWireNames.State(KodoState.Healthy));
        Assert.Equal("degraded", KodoWireNames.State(KodoState.Degraded));
        Assert.Equal("failing", KodoWireNames.State(KodoState.Failing));
        Assert.Equal("recovering", KodoWireNames.State(KodoState.Recovering));
        Assert.Equal("frozen", KodoWireNames.State(KodoState.Frozen));

        Assert.Equal("healthy", KodoWireNames.Display(KodoDisplayState.Healthy));
        Assert.Equal("degraded", KodoWireNames.Display(KodoDisplayState.Degraded));
        Assert.Equal("failing", KodoWireNames.Display(KodoDisplayState.Failing));
        Assert.Equal("recovering", KodoWireNames.Display(KodoDisplayState.Recovering));
        Assert.Equal("frozen", KodoWireNames.Display(KodoDisplayState.Frozen));
        Assert.Equal("unobserved", KodoWireNames.Display(KodoDisplayState.Unobserved));

        Assert.Equal("green", KodoWireNames.Probe(ProbeStatus.Green));
        Assert.Equal("amber", KodoWireNames.Probe(ProbeStatus.Amber));
        Assert.Equal("red", KodoWireNames.Probe(ProbeStatus.Red));
        Assert.Equal("unknown", KodoWireNames.Probe(ProbeStatus.Unknown));
    }

    [Fact]
    public void ServerStatesNeverMapToUnknownWireStrings()
    {
        foreach (KodoState state in Enum.GetValues<KodoState>())
        {
            Assert.NotEqual(KodoWireNames.Display(KodoDisplayState.Unobserved), KodoWireNames.State(state));
        }
    }
}
