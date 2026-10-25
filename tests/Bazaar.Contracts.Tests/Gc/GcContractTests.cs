using System.Text.Json;
using Bazaar.Contracts.Gc;

namespace Bazaar.Contracts.Tests.Gc;

public class GcContractTests
{
    private static readonly Guid _gcId = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _authorId = Guid.Parse("0196a6f0-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset _at = new(2026, 10, 25, 5, 36, 0, TimeSpan.Zero);

    private static GcEvent MessageEvent(long seq = 1) => new(
        _gcId, seq, Guid.Parse("0196a6f0-0000-7000-8000-000000000003"), GcEventKinds.Message,
        AuthorKind.Human, _authorId, SourceTrust.MemberHuman, TaintedAtWrite: false, TaintSourcesJson: null,
        MessageId: Guid.Parse("0196a6f0-0000-7000-8000-000000000004"), Version: 1, TargetSeq: null,
        ReplyToSeq: null, PayloadJson: "{\"text\":\"hello\"}", CreatedAt: _at);

    [Fact]
    public void Event_keeps_every_field_it_was_built_with()
    {
        var evt = MessageEvent(seq: 7);

        Assert.Equal(7, evt.Seq);
        Assert.Equal(GcEventKinds.Message, evt.Kind);
        Assert.Equal(SourceTrust.MemberHuman, evt.SourceTrust);
        Assert.False(evt.TaintedAtWrite);
        Assert.Null(evt.TaintSourcesJson);
        Assert.Equal(1, evt.Version);
        Assert.Equal("{\"text\":\"hello\"}", evt.PayloadJson);
    }

    [Fact]
    public void Events_with_equal_fields_are_equal()
    {
        Assert.Equal(MessageEvent(), MessageEvent());
        Assert.Equal(MessageEvent().GetHashCode(), MessageEvent().GetHashCode());
    }

    [Fact]
    public void Events_differing_in_one_field_are_not_equal()
    {
        Assert.NotEqual(MessageEvent(seq: 1), MessageEvent(seq: 2));
        Assert.NotEqual(MessageEvent() with { SourceTrust = SourceTrust.Agent }, MessageEvent());
    }

    [Fact]
    public void Event_round_trips_through_json()
    {
        var evt = MessageEvent() with
        {
            TaintedAtWrite = true,
            TaintSourcesJson = "[\"0196a6f0-0000-7000-8000-000000000005\"]",
            TargetSeq = null,
            ReplyToSeq = 3,
        };

        var json = JsonSerializer.Serialize(evt, GcJson.Options);
        var back = JsonSerializer.Deserialize<GcEvent>(json, GcJson.Options);

        Assert.Equal(evt, back);
    }

    [Fact]
    public void Event_round_trips_with_optional_fields_null()
    {
        var evt = new GcEvent(_gcId, 1, Guid.NewGuid(), GcEventKinds.System, AuthorKind.System,
            Guid.Empty, SourceTrust.System, false, null, null, null, null, null, null, _at);

        var back = JsonSerializer.Deserialize<GcEvent>(JsonSerializer.Serialize(evt, GcJson.Options), GcJson.Options);

        Assert.Equal(evt, back);
    }

    [Theory]
    [InlineData(SourceTrust.Owner, "\"owner\"")]
    [InlineData(SourceTrust.System, "\"system\"")]
    [InlineData(SourceTrust.MemberHuman, "\"member-human\"")]
    [InlineData(SourceTrust.Agent, "\"agent\"")]
    [InlineData(SourceTrust.External, "\"external\"")]
    [InlineData(SourceTrust.Quarantined, "\"quarantined\"")]
    public void Source_trust_uses_the_doc_15_wire_names(SourceTrust trust, string expected) =>
        Assert.Equal(expected, JsonSerializer.Serialize(trust, GcJson.Options));

    [Theory]
    [InlineData(AuthorKind.ExternalBridge, "\"external_bridge\"")]
    [InlineData(AuthorKind.Human, "\"human\"")]
    public void Author_kind_uses_its_wire_names(AuthorKind kind, string expected) =>
        Assert.Equal(expected, JsonSerializer.Serialize(kind, GcJson.Options));

    [Fact]
    public void Every_source_trust_value_round_trips()
    {
        foreach (var trust in Enum.GetValues<SourceTrust>())
        {
            var json = JsonSerializer.Serialize(trust, GcJson.Options);
            Assert.Equal(trust, JsonSerializer.Deserialize<SourceTrust>(json, GcJson.Options));
        }
    }

    [Fact]
    public void Principal_round_trips_and_compares_by_value()
    {
        var principal = new GcPrincipal(_authorId, PrincipalKind.Agent, "Charles");

        var back = JsonSerializer.Deserialize<GcPrincipal>(JsonSerializer.Serialize(principal, GcJson.Options), GcJson.Options);

        Assert.Equal(principal, back);
        Assert.Equal(new GcPrincipal(_authorId, PrincipalKind.Agent, "Charles"), principal);
        Assert.NotEqual(new GcPrincipal(_authorId, PrincipalKind.Human, "Charles"), principal);
    }

    [Fact]
    public void Principal_kind_serializes_as_its_wire_name() =>
        Assert.Equal("\"agent\"", JsonSerializer.Serialize(PrincipalKind.Agent, GcJson.Options));

    [Fact]
    public void Message_round_trips_and_reports_edits()
    {
        var message = new GcMessage(_gcId, Guid.NewGuid(), _authorId, 1, 2, "fixed text", Edited: true, CreatedAt: _at);

        var back = JsonSerializer.Deserialize<GcMessage>(JsonSerializer.Serialize(message, GcJson.Options), GcJson.Options);

        Assert.Equal(message, back);
        Assert.True(back!.Edited);
        Assert.NotEqual(message with { Edited = false }, message);
    }

    [Fact]
    public void Seeded_event_kinds_are_distinct_and_lowercase()
    {
        var kinds = new[]
        {
            GcEventKinds.Message, GcEventKinds.MessageEdit, GcEventKinds.System, GcEventKinds.Structured,
            GcEventKinds.Quarantine, GcEventKinds.Handover, GcEventKinds.ExternalMail,
        };

        Assert.Equal(kinds.Length, kinds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(kinds, kind => Assert.Equal(kind.ToLowerInvariant(), kind));
    }
}
