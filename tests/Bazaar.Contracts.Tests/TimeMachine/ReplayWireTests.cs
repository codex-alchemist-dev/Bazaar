using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.TimeMachine;

public class ReplayWireTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset _at = new(2026, 11, 18, 5, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, object> Samples() => new()
    {
        { "frame-request", new ReplayFrameRequest(_gc, 42, "default") },
        { "frame-request-no-mode", new ReplayFrameRequest(_gc, 42) },
        {
            "frame-result",
            new ReplayFrameResult(_gc, 40, 40, new ReplayFrameWire(
                [new ReplayMessageWire(_gc, _agent, 3, 5, 2, true, false, "{\"text\":\"hi\"}", _at)],
                new ReplayTasksPart(true, [new ReplayTaskWire(_gc, "Ship", "Review")], null),
                new ReplayVaultPart(true, new ReplayVaultWire("abc123"), null),
                new ReplaySoulsPart(false, null, "Soul versions not installed"),
                new ReplayReputationPart(true, [new ReplayReputationWire(_agent, 0.75m)], null)))
        },
        { "fork-request", new ReplayForkRequest(_gc, 42, _agent) },
        { "fork-result", new ReplayForkResult(_gc, "abc123", true) },
        { "fork-result-no-vault", new ReplayForkResult(_gc, null, false) },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Payload_round_trips(string name, object payload)
    {
        _ = name;
        string json = JsonSerializer.Serialize(payload, payload.GetType(), WireJsonContext.Default);
        object? back = JsonSerializer.Deserialize(json, payload.GetType(), WireJsonContext.Default);
        Assert.Equivalent(payload, back, strict: true);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Payload_ignores_unknown_fields_at_every_level(string name, object payload)
    {
        _ = name;
        var node = JsonNode.Parse(JsonSerializer.Serialize(payload, payload.GetType(), WireJsonContext.Default))!;
        AddUnknown(node);
        object? back = JsonSerializer.Deserialize(node.ToJsonString(), payload.GetType(), WireJsonContext.Default);
        Assert.Equivalent(payload, back, strict: true);
    }

    [Fact]
    public void Wire_names_are_the_doc_names_and_property_names_are_camel_case()
    {
        Assert.Equal("replay.frame", WireTypes.ReplayFrame);
        Assert.Equal("replay.fork", WireTypes.ReplayFork);
        var json = JsonSerializer.Serialize(new ReplayForkResult(_gc, null, true), WireJsonContext.Default.ReplayForkResult);
        Assert.Contains("\"newGc\"", json);
        Assert.DoesNotContain("vaultCommit", json);
    }

    private static void AddUnknown(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var child in o.Select(p => p.Value).Where(v => v is not null).ToList())
                {
                    AddUnknown(child!);
                }

                o["zzUnknown"] = 1;
                break;
            case JsonArray a:
                foreach (var child in a.Where(v => v is not null).ToList())
                {
                    AddUnknown(child!);
                }

                break;
        }
    }
}
