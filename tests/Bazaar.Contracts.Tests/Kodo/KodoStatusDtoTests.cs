using System.Text.Json;
using Bazaar.Contracts.Kodo;

namespace Bazaar.Contracts.Tests.Kodo;

public class KodoStatusDtoTests
{
    private static readonly DateTimeOffset _asOf = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Status_serialises_with_camel_case_keys_and_wire_spellings()
    {
        var dto = new KodoStatusDto(
            "frozen",
            "failing",
            42,
            _asOf,
            [new KodoControlDto("incidentExit", true, null)]);

        var json = JsonSerializer.Serialize(dto, KodoContractsJsonContext.Default.KodoStatusDto);

        Assert.Contains("\"state\":\"frozen\"", json);
        Assert.Contains("\"underlyingState\":\"failing\"", json);
        Assert.Contains("\"score\":42", json);
        Assert.Contains("\"asOf\":\"2026-11-13T12:00:00+00:00\"", json);
        Assert.Contains("\"control\":\"incidentExit\"", json);
        Assert.DoesNotContain("disabledReason", json);
    }

    [Fact]
    public void Status_round_trips_through_the_generated_context()
    {
        var dto = new KodoStatusDto(
            "degraded",
            null,
            73,
            _asOf,
            [
                new KodoControlDto("restart", false, "Incident mode is active; risky actions are blocked"),
                new KodoControlDto("rollback", true, null),
            ]);

        var json = JsonSerializer.Serialize(dto, KodoContractsJsonContext.Default.KodoStatusDto);
        var back = JsonSerializer.Deserialize(json, KodoContractsJsonContext.Default.KodoStatusDto);

        Assert.NotNull(back);
        Assert.Equal(dto.State, back!.State);
        Assert.Equal(dto.UnderlyingState, back.UnderlyingState);
        Assert.Equal(dto.Score, back.Score);
        Assert.Equal(dto.AsOf, back.AsOf);
        Assert.Equal(dto.Controls, back.Controls.ToList());
    }

    [Fact]
    public void Unknown_members_from_a_newer_server_are_skipped()
    {
        const string json = """
            {"state":"healthy","score":100,"asOf":"2026-11-13T12:00:00+00:00",
             "controls":[],"futureField":{"anything":true}}
            """;

        var back = JsonSerializer.Deserialize(json, KodoContractsJsonContext.Default.KodoStatusDto);

        Assert.NotNull(back);
        Assert.Equal("healthy", back!.State);
        Assert.Null(back.UnderlyingState);
        Assert.Empty(back.Controls);
    }
}
