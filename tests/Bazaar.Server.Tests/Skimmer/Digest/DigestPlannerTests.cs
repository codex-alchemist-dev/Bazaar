using System.Text.Json;
using Bazaar.Server.Agents.Inbox;
using Bazaar.Server.Skimmer.Digest;

namespace Bazaar.Server.Tests.Skimmer.Digest;

public class DigestPlannerTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _digestAt = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid _agent = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid _gc = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private static GateDecision Decision(string decision, string path, long seq = 7, string? category = "status") =>
        new(_agent, _gc, seq, path, decision, category, 4200, Shadow: false);

    private static List<string> PayloadKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
    }

    [Fact]
    public void Defer_enqueues_gc_digest_at_digest_time_with_default_reason()
    {
        var item = DigestPlanner.Plan(Decision("defer", "default"), _now, _digestAt, DigestQuoteMode.Reference);

        Assert.NotNull(item);
        Assert.Equal(InboxKind.GcDigest, item.Kind);
        Assert.Equal(_agent, item.AgentId);
        Assert.Equal(_gc, item.GcId);
        Assert.Equal(7, item.Seq);
        Assert.Equal(_digestAt, item.DueAt);
        Assert.Contains("\"reason\":\"default\"", item.PayloadJson);
    }

    [Fact]
    public void Scored_wake_enqueues_skimmer_promoted_due_now()
    {
        var item = DigestPlanner.Plan(Decision("wake", "scored"), _now, _digestAt, DigestQuoteMode.Reference);

        Assert.NotNull(item);
        Assert.Equal(InboxKind.SkimmerPromoted, item.Kind);
        Assert.Equal(_now, item.DueAt);
    }

    [Theory]
    [InlineData("mention")]
    [InlineData("dm")]
    [InlineData("critical")]
    [InlineData("default")]
    public void Normal_wakes_take_the_wake_path_and_enqueue_nothing(string path)
    {
        var item = DigestPlanner.Plan(Decision("wake", path), _now, _digestAt, DigestQuoteMode.Reference);

        Assert.Null(item);
    }

    [Fact]
    public void Quarantine_defer_enqueues_gc_digest_marked_quarantined()
    {
        var item = DigestPlanner.Plan(Decision("defer", "quarantine"), _now, _digestAt, DigestQuoteMode.Reference);

        Assert.NotNull(item);
        Assert.Equal(InboxKind.GcDigest, item.Kind);
        Assert.Equal(_digestAt, item.DueAt);
        using var document = JsonDocument.Parse(item.PayloadJson);
        Assert.True(document.RootElement.GetProperty("quarantined").GetBoolean());
    }

    [Fact]
    public void Payload_has_exactly_the_documented_keys_and_no_text_field()
    {
        var item = DigestPlanner.Plan(Decision("defer", "default"), _now, _digestAt, DigestQuoteMode.Reference);

        Assert.NotNull(item);
        Assert.Equal(["gc_id", "seq", "category", "reason"], PayloadKeys(item.PayloadJson));
        Assert.DoesNotContain("text", item.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("excerpt", item.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quarantined_payload_adds_only_the_quarantined_key()
    {
        var item = DigestPlanner.Plan(Decision("defer", "quarantine"), _now, _digestAt, DigestQuoteMode.Reference);

        Assert.NotNull(item);
        Assert.Equal(["gc_id", "seq", "category", "reason", "quarantined"], PayloadKeys(item.PayloadJson));
    }

    [Fact]
    public void Null_category_is_written_as_json_null()
    {
        var item = DigestPlanner.Plan(Decision("defer", "default", category: null), _now, _digestAt, DigestQuoteMode.Reference);

        Assert.NotNull(item);
        using var document = JsonDocument.Parse(item.PayloadJson);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("category").ValueKind);
    }

    [Fact]
    public void Digest_time_in_the_past_is_used_as_given()
    {
        var past = _now.AddHours(-3);

        var item = DigestPlanner.Plan(Decision("defer", "default"), _now, past, DigestQuoteMode.Reference);

        Assert.NotNull(item);
        Assert.Equal(past, item.DueAt);
    }

    [Fact]
    public void Idempotency_key_is_stable_and_follows_the_documented_format()
    {
        var first = DigestPlanner.Plan(Decision("defer", "default"), _now, _digestAt, DigestQuoteMode.Reference);
        var second = DigestPlanner.Plan(Decision("defer", "default"), _now.AddMinutes(5), _digestAt.AddDays(1), DigestQuoteMode.Reference);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal($"{_agent:N}|{_gc:N}|7|gc_digest", first.IdempotencyKey);
    }

    [Fact]
    public void Idempotency_key_differs_per_kind_and_per_seq()
    {
        var digest = DigestPlanner.Plan(Decision("defer", "default"), _now, _digestAt, DigestQuoteMode.Reference);
        var promoted = DigestPlanner.Plan(Decision("wake", "scored"), _now, _digestAt, DigestQuoteMode.Reference);
        var otherSeq = DigestPlanner.Plan(Decision("defer", "default", seq: 8), _now, _digestAt, DigestQuoteMode.Reference);

        Assert.NotNull(digest);
        Assert.NotNull(promoted);
        Assert.NotNull(otherSeq);
        Assert.NotEqual(digest.IdempotencyKey, promoted.IdempotencyKey);
        Assert.NotEqual(digest.IdempotencyKey, otherSeq.IdempotencyKey);
    }

    [Fact]
    public void Seq_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DigestPlanner.Plan(Decision("defer", "default", seq: 0), _now, _digestAt, DigestQuoteMode.Reference));
    }

    [Fact]
    public void Null_decision_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DigestPlanner.Plan(null!, _now, _digestAt, DigestQuoteMode.Reference));
    }

    [Fact]
    public void Unknown_decision_or_path_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            DigestPlanner.Plan(Decision("maybe", "default"), _now, _digestAt, DigestQuoteMode.Reference));
        Assert.Throws<ArgumentException>(() =>
            DigestPlanner.Plan(Decision("defer", "unknown"), _now, _digestAt, DigestQuoteMode.Reference));
    }

    [Fact]
    public void Encrypted_excerpt_mode_throws_until_doc_14_lands()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            DigestPlanner.Plan(Decision("defer", "default"), _now, _digestAt, DigestQuoteMode.EncryptedExcerpt));

        Assert.Equal("encrypted_excerpt needs the per-person key (doc 14); not implemented", exception.Message);
    }
}
