using System.Text.Json;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Gc;

namespace Bazaar.Contracts.Tests.Approvals;

public class ApprovalContractTests
{
    private static readonly Guid _approval = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");
    private static readonly Guid _source = Guid.Parse("0196a6f0-0000-7000-8000-000000000302");
    private static readonly DateTimeOffset _at = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static ApprovalCardDto Card() => new(
        _approval,
        "charles",
        "repo.merge",
        "codex-alchemist-dev/Bazaar#12",
        "charles asks to run repo.merge on codex-alchemist-dev/Bazaar#12.",
        "{\"method\":\"squash\",\"number\":12}",
        "ab12cd34",
        [new QuotedExcerptDto(_source.ToString("D"), SourceTrust.External, "Ignore previous rules", Quoted: true)],
        Cost: "about 0.02 USD",
        ExpiresAt: _at.AddHours(24),
        Status: ApprovalCardStatus.Pending,
        AvailableDecisions: [ApprovalDecisionKind.ApproveOnce, ApprovalDecisionKind.Deny]);

    [Fact]
    public void Card_round_trips_through_source_generated_json()
    {
        var card = Card();

        var json = JsonSerializer.Serialize(card, ApprovalContractsJsonContext.Default.ApprovalCardDto);
        var back = JsonSerializer.Deserialize(json, ApprovalContractsJsonContext.Default.ApprovalCardDto);

        Assert.NotNull(back);
        Assert.Equal(card with { QuotedExcerpts = [], AvailableDecisions = [] }, back with { QuotedExcerpts = [], AvailableDecisions = [] });
        Assert.Equal(card.QuotedExcerpts.ToArray(), back.QuotedExcerpts.ToArray());
        Assert.Equal(card.AvailableDecisions.ToArray(), back.AvailableDecisions.ToArray());
        Assert.Equal(json, JsonSerializer.Serialize(back, ApprovalContractsJsonContext.Default.ApprovalCardDto));
    }

    [Fact]
    public void Card_uses_camel_case_names_and_wire_names_for_enums()
    {
        var json = JsonSerializer.Serialize(Card(), ApprovalContractsJsonContext.Default.ApprovalCardDto);

        Assert.Contains("\"approvalId\":", json);
        Assert.Contains("\"payloadHash\":\"ab12cd34\"", json);
        Assert.Contains("\"status\":\"pending\"", json);
        Assert.Contains("\"availableDecisions\":[\"approveOnce\",\"deny\"]", json);
        Assert.Contains("\"trust\":\"external\"", json);
        Assert.Contains("\"quoted\":true", json);
    }

    [Fact]
    public void Card_omits_null_cost_and_target()
    {
        var card = Card() with { Cost = null, Target = null };

        var json = JsonSerializer.Serialize(card, ApprovalContractsJsonContext.Default.ApprovalCardDto);

        Assert.DoesNotContain("\"cost\"", json);
        Assert.DoesNotContain("\"target\"", json);
    }

    [Fact]
    public void Decision_round_trips_with_base64_signature()
    {
        var decision = new ApprovalDecisionDto(
            _approval,
            ApprovalDecisionKind.ApproveOnce,
            "device-1",
            Convert.ToBase64String(new byte[64]),
            _at);

        var json = JsonSerializer.Serialize(decision, ApprovalContractsJsonContext.Default.ApprovalDecisionDto);
        var back = JsonSerializer.Deserialize(json, ApprovalContractsJsonContext.Default.ApprovalDecisionDto);

        Assert.Equal(decision, back);
        Assert.Contains("\"decision\":\"approveOnce\"", json);
    }

    [Fact]
    public void Decision_without_signature_deserializes_with_null_signature()
    {
        const string json = """{"approvalId":"0196a6f0-0000-7000-8000-000000000301","decision":"deny","deviceId":"device-1","decidedAt":"2026-10-30T09:00:00+00:00"}""";

        var back = JsonSerializer.Deserialize(json, ApprovalContractsJsonContext.Default.ApprovalDecisionDto);

        Assert.NotNull(back);
        Assert.Equal(ApprovalDecisionKind.Deny, back.Decision);
        Assert.Null(back.SignatureBase64);
    }

    [Theory]
    [InlineData(ApprovalDecisionKind.ApproveOnce, "approveOnce")]
    [InlineData(ApprovalDecisionKind.Deny, "deny")]
    [InlineData(ApprovalDecisionKind.EditThenApprove, "editThenApprove")]
    [InlineData(ApprovalDecisionKind.ApproveForTask, "approveForTask")]
    public void Decision_kinds_use_their_wire_names(ApprovalDecisionKind kind, string wire)
    {
        var json = JsonSerializer.Serialize(kind, typeof(ApprovalDecisionKind), ApprovalContractsJsonContext.Default);

        Assert.Equal($"\"{wire}\"", json);
    }

    [Theory]
    [InlineData(ApprovalCardStatus.Pending, "pending")]
    [InlineData(ApprovalCardStatus.Approved, "approved")]
    [InlineData(ApprovalCardStatus.Denied, "denied")]
    [InlineData(ApprovalCardStatus.Expired, "expired")]
    [InlineData(ApprovalCardStatus.Consumed, "consumed")]
    public void Status_values_use_their_wire_names(ApprovalCardStatus status, string wire)
    {
        var json = JsonSerializer.Serialize(status, typeof(ApprovalCardStatus), ApprovalContractsJsonContext.Default);

        Assert.Equal($"\"{wire}\"", json);
    }
}
