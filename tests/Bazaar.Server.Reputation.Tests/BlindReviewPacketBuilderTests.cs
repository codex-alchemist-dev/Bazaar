using System.Reflection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Reputation.Tests;

public class BlindReviewPacketBuilderTests
{
    private static readonly AuthorIdentity _author = new("agent-charles", "Charles Dee", "Haiku 5.5", "Claude");

    private static ReviewSubject Subject(string title, string description = "", string rubric = "") =>
        new("card-1", "pr-42", "diff-42", title, description, rubric, _author);

    private static ReviewPacketResult Build(ReviewSubject s) => new BlindReviewPacketBuilder().Build(s);

    [Fact]
    public void Packet_type_has_no_author_or_model_members()
    {
        string[] banned = ["author", "agent", "model", "badge", "family", "displayname"];
        var names = typeof(ReviewPacket)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name.ToLowerInvariant());
        foreach (var name in names)
        {
            Assert.DoesNotContain(banned, b => name.Contains(b, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Strips_name_badge_id_and_family_from_text()
    {
        var r = Build(Subject("Fix by Charles Dee", "Written by agent-charles on Haiku 5.5 (Claude).", "Rate Charles Dee's work"));
        Assert.Equal("Fix by [redacted]", r.Packet.Title);
        Assert.Equal("Written by [redacted] on [redacted] ([redacted]).", r.Packet.Description);
        Assert.Equal("Rate [redacted]'s work", r.Packet.RubricText);
        Assert.Equal(5, r.RedactionCount);
    }

    [Theory]
    [InlineData("by CHARLES DEE")]
    [InlineData("by charles   dee")]
    [InlineData("by Charles\tDee")]
    [InlineData("by Charles\nDee")]
    public void Matches_case_and_whitespace_variants(string text)
    {
        var r = Build(Subject(text));
        Assert.Equal("by [redacted]", r.Packet.Title);
        Assert.Equal(1, r.RedactionCount);
    }

    [Fact]
    public void Does_not_redact_inside_longer_words()
    {
        var subject = Subject("Algorithm fix") with { Author = _author with { DisplayName = "Al" } };
        var r = Build(subject);
        Assert.Equal("Algorithm fix", r.Packet.Title);
        Assert.Equal(0, r.RedactionCount);
    }

    [Fact]
    public void No_op_when_nothing_present()
    {
        var r = Build(Subject("Add retry", "Retries three times.", "Is it correct?"));
        Assert.Equal("Add retry", r.Packet.Title);
        Assert.Equal("Retries three times.", r.Packet.Description);
        Assert.Equal("Is it correct?", r.Packet.RubricText);
        Assert.Equal(0, r.RedactionCount);
    }

    [Fact]
    public void Keeps_refs_and_stamps_time()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));
        var r = new BlindReviewPacketBuilder(clock).Build(Subject("t"));
        Assert.Equal("card-1", r.Packet.WorkItemRef);
        Assert.Equal("pr-42", r.Packet.SubjectRef);
        Assert.Equal("diff-42", r.Packet.ContentRef);
        Assert.Equal(clock.GetUtcNow(), r.Packet.CreatedAt);
        Assert.NotEqual(Guid.Empty, r.Packet.PacketId);
    }

    [Fact]
    public void Blank_identity_fields_are_ignored()
    {
        var subject = Subject("Hello world") with { Author = new AuthorIdentity("", " ", "", null) };
        var r = Build(subject);
        Assert.Equal("Hello world", r.Packet.Title);
        Assert.Equal(0, r.RedactionCount);
    }
}
