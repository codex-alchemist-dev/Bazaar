using System.Text.Json;

using Bazaar.Contracts.Memory;

namespace Bazaar.Contracts.Tests.Memory;

public class MemoryToolContractsTests
{
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, MemoryJson.Options);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, MemoryJson.Options) ?? throw new InvalidOperationException("null JSON");

    [Fact]
    public void Store_request_round_trips()
    {
        var request = new MemoryStoreRequest(
            AgentId: "agent-charles",
            Scope: "private",
            Kind: "failure",
            Title: "Build broke on missing SDK",
            Body: "Install the pinned SDK before building.",
            Origin: "agent",
            TurnId: "turn-7");

        var json = Serialize(request);
        var back = Deserialize<MemoryStoreRequest>(json);

        Assert.Equal(request, back);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Store_result_round_trips()
    {
        var result = new MemoryStoreResult(
            NoteId: "note-1",
            Path: "agents/charles/memory/failures/build-broke.md",
            Version: 1,
            Tier: "untrusted_derived",
            Flagged: true);

        var back = Deserialize<MemoryStoreResult>(Serialize(result));

        Assert.Equal(result, back);
    }

    [Fact]
    public void Retrieve_request_round_trips_with_kind_filter()
    {
        var request = new MemoryRetrieveRequest(
            AgentId: "agent-charles",
            QueryText: "pinned sdk",
            Scope: "project",
            Kinds: ["procedural", "failure"],
            TokenBudget: 800,
            MaxHits: 5);

        var json = Serialize(request);
        var back = Deserialize<MemoryRetrieveRequest>(json);

        Assert.Equal(request.Kinds, back.Kinds);
        Assert.Equal(request.QueryText, back.QueryText);
        Assert.Equal(request.Scope, back.Scope);
        Assert.Equal(request.TokenBudget, back.TokenBudget);
        Assert.Equal(request.MaxHits, back.MaxHits);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Retrieve_request_without_kind_filter_round_trips_null()
    {
        var request = new MemoryRetrieveRequest("agent-a", "query", "private", null, 500, 3);

        var back = Deserialize<MemoryRetrieveRequest>(Serialize(request));

        Assert.Null(back.Kinds);
        Assert.Equal(request, back);
    }

    [Fact]
    public void Retrieve_result_round_trips_with_labelled_hits()
    {
        var result = new MemoryRetrieveResult(
            Hits:
            [
                new MemoryHitDto("note-1", "Pinned SDK", "project", "owner_promoted", "procedural", "owner", "Use the pinned SDK.", false),
                new MemoryHitDto("note-2", "Scraped tip", "project", "untrusted_derived", "semantic", "external", "Quoted text.", true),
            ],
            LexicalOnly: true,
            TokensUsed: 42);

        var json = Serialize(result);
        var back = Deserialize<MemoryRetrieveResult>(json);

        Assert.Equal(result.Hits, back.Hits);
        Assert.True(back.Hits[1].Labelled);
        Assert.False(back.Hits[0].Labelled);
        Assert.True(back.LexicalOnly);
        Assert.Equal(42, back.TokensUsed);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Update_request_round_trips_with_body_and_field_changes()
    {
        var request = new MemoryUpdateRequest(
            AgentId: "agent-charles",
            NoteId: "note-1",
            ExpectedVersion: 3,
            Body: "Revised body.",
            FieldChanges: [new MemoryFieldChange("status", "active"), new MemoryFieldChange("tags", "sdk")]);

        var json = Serialize(request);
        var back = Deserialize<MemoryUpdateRequest>(json);

        Assert.Equal(3, back.ExpectedVersion);
        Assert.Equal("Revised body.", back.Body);
        Assert.Equal(request.FieldChanges, back.FieldChanges);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Update_request_with_only_front_matter_round_trips_null_body()
    {
        var request = new MemoryUpdateRequest("agent-a", "note-1", 1, null, [new MemoryFieldChange("status", "active")]);

        var back = Deserialize<MemoryUpdateRequest>(Serialize(request));

        Assert.Null(back.Body);
        Assert.Equal(request.NoteId, back.NoteId);
        Assert.Equal(request.ExpectedVersion, back.ExpectedVersion);
        Assert.Equal(request.FieldChanges, back.FieldChanges);
    }

    [Fact]
    public void Update_result_round_trips_on_conflict()
    {
        var result = new MemoryUpdateResult("note-1", Updated: false, Version: 4, Conflict: true);

        var back = Deserialize<MemoryUpdateResult>(Serialize(result));

        Assert.Equal(result, back);
        Assert.True(back.Conflict);
    }

    [Fact]
    public void Deprecate_request_round_trips()
    {
        var request = new MemoryDeprecateRequest("agent-charles", "note-1", "Superseded by note-9.");

        var json = Serialize(request);
        var back = Deserialize<MemoryDeprecateRequest>(json);

        Assert.Equal(request, back);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Deprecate_result_round_trips()
    {
        var result = new MemoryDeprecateResult("note-1", Version: 5, Deprecated: true);

        var back = Deserialize<MemoryDeprecateResult>(Serialize(result));

        Assert.Equal(result, back);
    }
}
