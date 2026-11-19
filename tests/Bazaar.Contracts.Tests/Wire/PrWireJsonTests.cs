using System.Text.Json;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Wire;

/// <summary>The client PR wire records through <see cref="WireJsonContext"/> (doc 21). Pure: no sockets, no database.</summary>
public class PrWireJsonTests
{
    [Fact]
    public void Merge_status_travels_as_the_snake_case_name_the_doc_uses()
    {
        var reply = new PrMergeReply(PrMergeStatus.ChecksFailed, "abc", null);

        string json = JsonSerializer.Serialize(reply, WireJsonContext.Default.PrMergeReply);

        Assert.Contains("\"status\":\"checks_failed\"", json, StringComparison.Ordinal);
        Assert.Equal(reply, JsonSerializer.Deserialize(json, WireJsonContext.Default.PrMergeReply));
    }

    [Fact]
    public void Merge_request_round_trips_with_its_signature_and_nonce()
    {
        var payload = new PrOwnerPayload(
            "pr.merge", Guid.NewGuid(), [1, 2, 3], "abc", Guid.NewGuid(), "device-1",
            new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 19, 12, 5, 0, TimeSpan.Zero), [9, 8, 7]);
        var request = new PrMergeRequest("PR_1", "abc", "title", "message", payload, [4, 5, 6]);

        string json = JsonSerializer.Serialize(request, WireJsonContext.Default.PrMergeRequest);
        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.PrMergeRequest)!;

        Assert.Equal(request.Signature, back.Signature);
        Assert.Equal(payload.Nonce, back.Owner.Nonce);
        Assert.Equal(payload.ArgsHash, back.Owner.ArgsHash);
        Assert.Equal(payload.ExpiresAt, back.Owner.ExpiresAt);
    }

    [Fact]
    public void Hunks_reply_and_list_reply_round_trip()
    {
        var hunks = new PrHunksReply(PrHunksStatus.Stale, "def", "a.cs", [], null);
        var list = new PrListReply(
            [new PrListRow("PR_1", "o/r", 7, "t", "bot", PrAuthorKind.Agent, true, "abc", PrChecksRollup.Passing, 3)], "next");

        string hunksJson = JsonSerializer.Serialize(hunks, WireJsonContext.Default.PrHunksReply);
        string listJson = JsonSerializer.Serialize(list, WireJsonContext.Default.PrListReply);

        Assert.Contains("\"status\":\"stale\"", hunksJson, StringComparison.Ordinal);
        Assert.Contains("\"authorKind\":\"agent\"", listJson, StringComparison.Ordinal);
        Assert.Equal(PrChecksRollup.Passing, JsonSerializer.Deserialize(listJson, WireJsonContext.Default.PrListReply)!.Rows[0].Checks);
    }

    [Fact]
    public void Message_types_are_the_names_in_the_doc()
    {
        Assert.Equal(["pr.list", "pr.summary", "pr.hunks", "pr.review", "pr.merge"],
            [WireTypes.PrList, WireTypes.PrSummary, WireTypes.PrHunks, WireTypes.PrReview, WireTypes.PrMerge]);
    }
}
