using System.Collections.Immutable;
using System.Text.Json;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using static Bazaar.Server.Tests.Security.Approvals.ApprovalTestData;

namespace Bazaar.Server.Tests.Security.Approvals;

public sealed class ApprovalCardMapperTests
{
    private static readonly Guid _taintSource = Guid.Parse("0196a6f0-0000-7000-8000-000000000401");

    private static ApprovalRequest Stored(
        ApprovalStatus status = ApprovalStatus.Pending,
        ImmutableArray<TaintSource> taint = default,
        DateTimeOffset? expiresAt = null) => new(
            Guid.Parse("0196a6f0-0000-7000-8000-000000000402"),
            "charles",
            Session,
            "repo.merge",
            "codex-alchemist-dev/Bazaar#12",
            DispatchHash.Compute(Context()),
            """{"number":12,"method":"squash"}""",
            taint.IsDefault ? [] : taint,
            status,
            Now,
            expiresAt ?? Now.AddHours(24));

    [Fact]
    public void Pending_card_carries_the_exact_payload_and_the_supported_decisions()
    {
        var card = ApprovalCardMapper.ToCard(Stored(), Now, cost: "about 0.02 USD");

        Assert.Equal(Guid.Parse("0196a6f0-0000-7000-8000-000000000402"), card.ApprovalId);
        Assert.Equal("charles", card.AgentId);
        Assert.Equal("repo.merge", card.Tool);
        Assert.Equal("codex-alchemist-dev/Bazaar#12", card.Target);
        Assert.Equal("charles asks to run repo.merge on codex-alchemist-dev/Bazaar#12.", card.Summary);
        Assert.Equal("""{"number":12,"method":"squash"}""", card.PayloadText);
        Assert.Equal("about 0.02 USD", card.Cost);
        Assert.Equal(Now.AddHours(24), card.ExpiresAt);
        Assert.Equal(ApprovalCardStatus.Pending, card.Status);
        Assert.Equal(
            [ApprovalDecisionKind.ApproveOnce, ApprovalDecisionKind.Deny],
            card.AvailableDecisions.ToArray());
        Assert.Empty(card.QuotedExcerpts);
    }

    [Fact]
    public async Task Changing_args_changes_the_payload_hash_and_the_card_shows_the_dispatch_hash()
    {
        var store = new InMemoryApprovalStore();
        var service = new ApprovalService(store, new InMemoryOwnerKeyRegistry(), new InMemoryRateBucketStore(), new ApprovalOptions());
        var first = Context(args: """{"number":12}""");
        var second = Context(args: """{"number":13}""");

        var a = Present((await service.RequestAsync(first, Ct)).Request);
        var b = Present((await service.RequestAsync(second, Ct)).Request);

        var cardA = ApprovalCardMapper.ToCard(a, Now);
        var cardB = ApprovalCardMapper.ToCard(b, Now);

        Assert.Equal(Convert.ToHexStringLower(DispatchHash.Compute(first)), cardA.PayloadHash);
        Assert.Equal(Convert.ToHexStringLower(DispatchHash.Compute(second)), cardB.PayloadHash);
        Assert.NotEqual(cardA.PayloadHash, cardB.PayloadHash);
        Assert.NotEqual(cardA.PayloadText, cardB.PayloadText);
    }

    [Fact]
    public void Taint_excerpts_are_always_quoted_and_long_text_is_clipped()
    {
        var taint = ImmutableArray.Create(new TaintSource(_taintSource, ContentTrust.External, TaintState.Hard));
        var text = new string('x', ApprovalCardMapper.MaxExcerptLength + 500);
        var excerpts = new Dictionary<Guid, string> { [_taintSource] = text };

        var card = ApprovalCardMapper.ToCard(Stored(taint: taint), Now, excerpts);

        var excerpt = Assert.Single(card.QuotedExcerpts);
        Assert.True(excerpt.Quoted);
        Assert.Equal(SourceTrust.External, excerpt.Trust);
        Assert.Equal(_taintSource.ToString("D"), excerpt.SourceId);
        Assert.Equal(new string('x', ApprovalCardMapper.MaxExcerptLength) + "...", excerpt.Text);
    }

    [Fact]
    public void Taint_source_without_text_keeps_its_lineage_with_null_text()
    {
        var taint = ImmutableArray.Create(new TaintSource(_taintSource, ContentTrust.Agent, TaintState.Soft));

        var card = ApprovalCardMapper.ToCard(Stored(taint: taint), Now);

        var excerpt = Assert.Single(card.QuotedExcerpts);
        Assert.Null(excerpt.Text);
        Assert.True(excerpt.Quoted);
        Assert.Equal(SourceTrust.Agent, excerpt.Trust);
    }

    [Fact]
    public void Pending_request_past_its_expiry_shows_expired_with_no_decisions()
    {
        var card = ApprovalCardMapper.ToCard(Stored(expiresAt: Now.AddMinutes(-1)), Now);

        Assert.Equal(ApprovalCardStatus.Expired, card.Status);
        Assert.Empty(card.AvailableDecisions);
    }

    [Theory]
    [InlineData(ApprovalStatus.Approved, ApprovalCardStatus.Approved)]
    [InlineData(ApprovalStatus.Denied, ApprovalCardStatus.Denied)]
    [InlineData(ApprovalStatus.Expired, ApprovalCardStatus.Expired)]
    [InlineData(ApprovalStatus.Consumed, ApprovalCardStatus.Consumed)]
    public void Decided_requests_show_their_status_with_no_decisions(ApprovalStatus stored, ApprovalCardStatus expected)
    {
        var card = ApprovalCardMapper.ToCard(Stored(status: stored), Now);

        Assert.Equal(expected, card.Status);
        Assert.Empty(card.AvailableDecisions);
    }

    [Fact]
    public void Mapped_card_round_trips_through_source_generated_json()
    {
        var taint = ImmutableArray.Create(new TaintSource(_taintSource, ContentTrust.External, TaintState.Hard));
        var card = ApprovalCardMapper.ToCard(Stored(taint: taint), Now, new Dictionary<Guid, string> { [_taintSource] = "quoted" }, "0.02 USD");

        var json = JsonSerializer.Serialize(card, ApprovalContractsJsonContext.Default.ApprovalCardDto);
        var back = JsonSerializer.Deserialize(json, ApprovalContractsJsonContext.Default.ApprovalCardDto);

        Assert.NotNull(back);
        Assert.Equal(json, JsonSerializer.Serialize(back, ApprovalContractsJsonContext.Default.ApprovalCardDto));
        Assert.Equal(card.PayloadHash, back.PayloadHash);
    }
}
