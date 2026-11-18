using System.Text;
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
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Healthy), 100, [], 0, _now, null);

        string json = Encoding.UTF8.GetString(KodoApiCodec.EncodeSnapshot(snapshot));

        Assert.Equal(
            "{\"schemaVersion\":1,\"revision\":0,\"generatedAt\":\"2026-11-18T12:00:00+00:00\","
            + "\"state\":\"healthy\",\"underlying\":\"healthy\",\"score\":100,\"journalHead\":null,\"components\":[]}",
            json);
    }

    [Fact]
    public void FrozenSnapshotWithComponentsAndJournalHeadHasAStableEncoding()
    {
        KodoStatusSnapshot snapshot = KodoStatusSnapshotBuilder.Build(
            KodoApiTestData.Evaluation(KodoState.Frozen, KodoState.Failing),
            score: 49,
            components:
            [
                new KodoComponentInput("db.backup_freshness", ProbeStatus.Red, "timeout", _now.AddMinutes(-2)),
                new KodoComponentInput("host.disk.free", ProbeStatus.Green, null, _now),
            ],
            revision: 12,
            generatedAt: _now,
            journalHead: KodoApiTestData.JournalHead());

        string json = Encoding.UTF8.GetString(KodoApiCodec.EncodeSnapshot(snapshot));

        Assert.Equal(
            "{\"schemaVersion\":1,\"revision\":12,\"generatedAt\":\"2026-11-18T12:00:00+00:00\","
            + "\"state\":\"frozen\",\"underlying\":\"failing\",\"score\":49,"
            + "\"journalHead\":\"" + KodoApiTestData.JournalHeadHex + "\","
            + "\"components\":["
            + "{\"id\":\"db.backup_freshness\",\"state\":\"red\",\"cause\":\"timeout\",\"since\":\"2026-11-18T11:58:00+00:00\"},"
            + "{\"id\":\"host.disk.free\",\"state\":\"green\",\"cause\":null,\"since\":\"2026-11-18T12:00:00+00:00\"}]}",
            json);
    }

    [Fact]
    public void EncodingIsDeterministicForTheSameSnapshot()
    {
        KodoStatusSnapshot snapshot = KodoApiTestData.Snapshot(3);

        Assert.Equal(KodoApiCodec.EncodeSnapshot(snapshot), KodoApiCodec.EncodeSnapshot(snapshot));
    }

    [Fact]
    public void StateChangedEventHasAFixedShape()
    {
        KodoStreamWriter writer = new();
        KodoStreamEvent streamEvent = writer.StateChanged(
            KodoState.Healthy, KodoApiTestData.Evaluation(KodoState.Degraded), _now);

        string json = Encoding.UTF8.GetString(KodoApiCodec.EncodeEvent(streamEvent));

        Assert.Equal(
            "{\"revision\":1,\"type\":\"state_changed\",\"at\":\"2026-11-18T12:00:00+00:00\","
            + "\"stateChange\":{\"previous\":\"healthy\",\"state\":\"degraded\",\"underlying\":\"degraded\"},\"snapshot\":null}",
            json);
    }

    [Fact]
    public void HeartbeatEventHasNoPayload()
    {
        KodoStreamWriter writer = new();

        string json = Encoding.UTF8.GetString(KodoApiCodec.EncodeEvent(writer.Heartbeat(_now)));

        Assert.Equal(
            "{\"revision\":1,\"type\":\"heartbeat\",\"at\":\"2026-11-18T12:00:00+00:00\",\"stateChange\":null,\"snapshot\":null}",
            json);
    }

    [Fact]
    public void SnapshotEventCarriesTheSnapshotAndItsRevision()
    {
        KodoStreamWriter writer = new();
        KodoStatusSnapshot snapshot = KodoApiTestData.Snapshot(writer.NextRevision);

        string json = Encoding.UTF8.GetString(KodoApiCodec.EncodeEvent(writer.Snapshot(snapshot)));

        Assert.StartsWith("{\"revision\":1,\"type\":\"snapshot\",\"at\":\"2026-11-18T12:00:00+00:00\",\"stateChange\":null,\"snapshot\":{\"schemaVersion\":1,\"revision\":1,", json);
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
