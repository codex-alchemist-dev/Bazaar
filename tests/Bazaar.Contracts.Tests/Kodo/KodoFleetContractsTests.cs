using System.Text;
using Bazaar.Contracts.Kodo;

namespace Bazaar.Contracts.Tests.Kodo;

public class KodoFleetContractsTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fleet_round_trips()
    {
        var fleet = new KodoFleetSnapshotDto(
            KodoFleetSnapshotDto.CurrentSchemaVersion,
            9,
            _at,
            [
                new KodoServerStatusDto("a", "Alpha", "frozen", "failing", 40, _at, "0.1.0+abc", 3, true),
                new KodoServerStatusDto("b", "Beta", "healthy", null, 100, _at, null, 0, false),
            ]);

        var back = KodoWireCodec.DecodeFleet(KodoWireCodec.EncodeFleet(fleet));

        Assert.NotNull(back);
        Assert.Equal(fleet.Revision, back!.Revision);
        Assert.Equal(fleet.AsOf, back.AsOf);
        Assert.Equal(fleet.Servers, back.Servers.ToList());
    }

    [Fact]
    public void Fleet_json_is_camel_case_and_omits_nulls()
    {
        var fleet = new KodoFleetSnapshotDto(
            1, 0, _at, [new KodoServerStatusDto("b", "Beta", "healthy", null, 100, _at, null, 0, false)]);

        var json = Encoding.UTF8.GetString(KodoWireCodec.EncodeFleet(fleet));

        Assert.Equal(
            "{\"schemaVersion\":1,\"revision\":0,\"asOf\":\"2026-11-19T08:00:00+00:00\",\"servers\":[{\"serverId\":\"b\","
            + "\"displayName\":\"Beta\",\"state\":\"healthy\",\"score\":100,\"asOf\":\"2026-11-19T08:00:00+00:00\","
            + "\"openAlerts\":0,\"incidentOpen\":false}]}",
            json);
    }

    [Fact]
    public void Fleet_decode_skips_unknown_members()
    {
        const string json = "{\"schemaVersion\":2,\"revision\":4,\"asOf\":\"2026-11-19T08:00:00+00:00\",\"future\":1,\"servers\":[]}";

        var back = KodoWireCodec.DecodeFleet(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(back);
        Assert.Equal(4, back!.Revision);
        Assert.Empty(back.Servers);
    }

    [Fact]
    public void Fleet_decode_of_null_is_null()
    {
        Assert.Null(KodoWireCodec.DecodeFleet("null"u8));
    }
}
