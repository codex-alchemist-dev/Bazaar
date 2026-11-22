using System.Text;
using Bazaar.Contracts.Kodo;

namespace Bazaar.Contracts.Tests.Kodo;

public class KodoStreamContractsTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static KodoStatusSnapshotDto Snapshot() => new(
        KodoStatusSnapshotDto.CurrentSchemaVersion,
        7,
        new KodoStatusDto("frozen", "failing", 42, _at, [new KodoControlDto("incidentExit", true, null)]),
        "00ff",
        [new KodoComponentDto("host.disk.free", "red", "disk_usage_critical", _at)]);

    [Fact]
    public void Snapshot_reuses_the_status_dto_and_round_trips()
    {
        var snapshot = Snapshot();

        var back = KodoWireCodec.DecodeSnapshot(KodoWireCodec.EncodeSnapshot(snapshot));

        Assert.NotNull(back);
        Assert.Equal(snapshot.Revision, back!.Revision);
        Assert.Equal(snapshot.Status.State, back.Status.State);
        Assert.Equal("failing", back.Status.UnderlyingState);
        Assert.Equal(snapshot.Status.AsOf, back.Status.AsOf);
        Assert.Equal(snapshot.Status.Controls, back.Status.Controls.ToList());
        Assert.Equal(snapshot.Components, back.Components.ToList());
    }

    [Fact]
    public void Snapshot_json_nests_the_status_and_omits_nulls()
    {
        var snapshot = new KodoStatusSnapshotDto(
            1, 0, new KodoStatusDto("healthy", null, 100, _at, []), null, []);

        var json = Encoding.UTF8.GetString(KodoWireCodec.EncodeSnapshot(snapshot));

        Assert.Equal(
            "{\"schemaVersion\":1,\"revision\":0,\"status\":{\"state\":\"healthy\",\"score\":100,"
            + "\"asOf\":\"2026-11-18T12:00:00+00:00\",\"controls\":[]},\"components\":[]}",
            json);
    }

    [Fact]
    public void Event_round_trips_with_a_state_change()
    {
        var streamEvent = new KodoStreamEventDto(
            3, KodoStreamEventDto.StateChangedType, _at, new KodoStateChangeDto("healthy", "frozen", "failing"), null);

        var back = KodoWireCodec.DecodeEvent(KodoWireCodec.EncodeEvent(streamEvent));

        Assert.Equal(streamEvent, back);
    }

    [Fact]
    public void Unknown_members_in_a_newer_event_are_skipped()
    {
        const string json = """
            {"revision":2,"type":"heartbeat","at":"2026-11-18T12:00:00+00:00","future":[1,2]}
            """;

        var back = KodoWireCodec.DecodeEvent(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(back);
        Assert.Equal(2, back!.Revision);
        Assert.Null(back.Snapshot);
        Assert.Null(back.StateChange);
    }

    [Fact]
    public void Encoders_reject_null()
    {
        Assert.Throws<ArgumentNullException>(() => KodoWireCodec.EncodeSnapshot(null!));
        Assert.Throws<ArgumentNullException>(() => KodoWireCodec.EncodeEvent(null!));
    }
}
