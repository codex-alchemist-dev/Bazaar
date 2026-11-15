using Bazaar.Server.Knowledge.Archive;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Knowledge.Archive;

public sealed class ArchiveChapterValidatorTests
{
    private static readonly Guid _docId = Guid.Parse("0196a7c4-0000-7000-8000-0000000000a1");

    private static readonly ArchiveRules _defaults = ArchiveRules.Defaults;

    [Fact]
    public void Sentence_without_a_ref_is_dropped_not_repaired()
    {
        var chapter = Validate(
            [new ArchiveSentence("Nothing cites this.", null), new ArchiveSentence("Blank ref.", "  ")],
            [Agent("m1", "Cited text.", 1)]);

        Assert.Empty(chapter.Sentences);
        Assert.Equal(2, chapter.Dropped[ArchiveDropReason.MissingRef]);
    }

    [Fact]
    public void Ref_to_a_source_that_does_not_exist_is_dropped()
    {
        var chapter = Validate(
            [new ArchiveSentence("Cites a ghost.", ArchiveSourceRef.GcMessage("gone"))],
            [Agent("m1", "Real text.", 1)]);

        Assert.Empty(chapter.Sentences);
        Assert.Equal(1, chapter.Dropped[ArchiveDropReason.UnknownSource]);
    }

    [Fact]
    public void Sentence_with_a_valid_ref_is_kept_in_order()
    {
        var chapter = Validate(
            [new ArchiveSentence("First.", ArchiveSourceRef.GcMessage("m1")), new ArchiveSentence("Second.", ArchiveSourceRef.GcEvent("e2"))],
            [Agent("m1", "First.", 1), Agent("e2", "Second.", 2, kind: "gc_event")]);

        Assert.Equal(["First.", "Second."], chapter.Sentences.Select(s => s.Text));
        Assert.Empty(chapter.Dropped);
    }

    [Fact]
    public void Human_text_is_excluded_by_default()
    {
        var chapter = Validate(
            [new ArchiveSentence("A person said this.", ArchiveSourceRef.GcMessage("h1"))],
            [Human("h1", "A person said this.", 1, gcOptedIn: true)],
            _defaults);

        Assert.Empty(chapter.Sentences);
        Assert.Equal(1, chapter.Dropped[ArchiveDropReason.HumanText]);
    }

    [Fact]
    public void Human_text_from_an_opted_in_gc_is_included_only_with_the_setting()
    {
        var draft = new ArchiveSentence("A person said this.", ArchiveSourceRef.GcMessage("h1"));
        var sources = new[] { Human("h1", "A person said this.", 1, gcOptedIn: true) };

        var excluded = Validate([draft], sources, new ArchiveRules(IncludeHumanText: false, IncludeCouncilReasons: true));
        var included = Validate([draft], sources, new ArchiveRules(IncludeHumanText: true, IncludeCouncilReasons: true));

        Assert.Empty(excluded.Sentences);
        Assert.Single(included.Sentences);
    }

    [Fact]
    public void Human_text_from_a_gc_that_did_not_opt_in_stays_out_even_with_the_setting()
    {
        var chapter = Validate(
            [new ArchiveSentence("Private room.", ArchiveSourceRef.GcMessage("h1"))],
            [Human("h1", "Private room.", 1, gcOptedIn: false)],
            new ArchiveRules(IncludeHumanText: true, IncludeCouncilReasons: true));

        Assert.Empty(chapter.Sentences);
        Assert.Equal(1, chapter.Dropped[ArchiveDropReason.HumanText]);
    }

    [Fact]
    public void Council_reason_before_the_joint_reveal_is_dropped_even_with_the_setting_on()
    {
        var chapter = Validate(
            [new ArchiveSentence("I voted no because.", ArchiveSourceRef.GcMessage("r1"))],
            [Reason("r1", "I voted no because.", 1, revealed: false)],
            _defaults);

        Assert.Empty(chapter.Sentences);
        Assert.Equal(1, chapter.Dropped[ArchiveDropReason.CouncilReasonBeforeReveal]);
    }

    [Fact]
    public void Council_reason_after_the_joint_reveal_is_kept_by_default()
    {
        var chapter = Validate(
            [new ArchiveSentence("I voted no because.", ArchiveSourceRef.GcMessage("r1"))],
            [Reason("r1", "I voted no because.", 1, revealed: true)],
            _defaults);

        Assert.Single(chapter.Sentences);
        Assert.Empty(chapter.Dropped);
    }

    [Fact]
    public void Council_reason_after_the_reveal_is_dropped_when_the_setting_is_off()
    {
        var chapter = Validate(
            [new ArchiveSentence("I voted no because.", ArchiveSourceRef.GcMessage("r1"))],
            [Reason("r1", "I voted no because.", 1, revealed: true)],
            new ArchiveRules(IncludeHumanText: false, IncludeCouncilReasons: false));

        Assert.Empty(chapter.Sentences);
        Assert.Equal(1, chapter.Dropped[ArchiveDropReason.CouncilReasonExcluded]);
    }

    [Fact]
    public void Thinking_gc_text_is_always_dropped_even_when_every_setting_is_open()
    {
        var chapter = Validate(
            [new ArchiveSentence("Musing aloud.", ArchiveSourceRef.GcMessage("t1"))],
            [Source("t1", "Musing aloud.", 1, ArchiveTextClass.Thinking, ContentTrust.Agent)],
            new ArchiveRules(IncludeHumanText: true, IncludeCouncilReasons: true));

        Assert.Empty(chapter.Sentences);
        Assert.Equal(1, chapter.Dropped[ArchiveDropReason.ThinkingGc]);
    }

    [Fact]
    public void Unsafe_text_that_would_break_the_markup_is_dropped()
    {
        var chapter = Validate(
            [new ArchiveSentence("Line one\nLine two", ArchiveSourceRef.GcMessage("m1")), new ArchiveSentence("Ends --> here", ArchiveSourceRef.GcMessage("m1"))],
            [Agent("m1", "Text.", 1)]);

        Assert.Empty(chapter.Sentences);
        Assert.Equal(2, chapter.Dropped[ArchiveDropReason.UnsafeText]);
    }

    [Fact]
    public void Chapter_takes_the_highest_trust_and_its_origin_from_accepted_sources()
    {
        var chapter = Validate(
            [new ArchiveSentence("Agent says.", ArchiveSourceRef.GcMessage("m1")), new ArchiveSentence("Owner says.", ArchiveSourceRef.GcMessage("o1"))],
            [Agent("m1", "Agent says.", 1), Source("o1", "Owner says.", 2, ArchiveTextClass.Agent, ContentTrust.Owner, MemoryTier.OwnerPromoted)]);

        Assert.Equal(MemoryTier.OwnerPromoted, chapter.Trust);
        Assert.Equal(ContentTrust.Owner, chapter.OriginKind);
    }

    [Fact]
    public void Dropped_sources_do_not_raise_the_chapter_trust()
    {
        var chapter = Validate(
            [new ArchiveSentence("Owner says.", ArchiveSourceRef.GcMessage("o1")), new ArchiveSentence("Agent says.", ArchiveSourceRef.GcMessage("m1"))],
            [Source("o1", "Owner says.", 1, ArchiveTextClass.Thinking, ContentTrust.Owner, MemoryTier.OwnerPromoted), Agent("m1", "Agent says.", 2)]);

        Assert.Equal(MemoryTier.AgentVerified, chapter.Trust);
        Assert.Equal(ContentTrust.Agent, chapter.OriginKind);
    }

    [Fact]
    public void Chapter_with_nothing_accepted_is_quarantined_and_system_origin()
    {
        var chapter = Validate([new ArchiveSentence("Nothing.", null)], []);

        Assert.Empty(chapter.Sentences);
        Assert.Equal(MemoryTier.Quarantined, chapter.Trust);
        Assert.Equal(ContentTrust.System, chapter.OriginKind);
    }

    [Fact]
    public void Two_sources_with_the_same_ref_are_rejected()
    {
        var sources = new[] { Agent("m1", "A.", 1), Agent("m1", "B.", 2) };

        Assert.Throws<ArgumentException>(() => Validate([], sources));
    }

    private static ValidatedArchiveChapter Validate(IReadOnlyList<ArchiveSentence> sentences, IEnumerable<ArchiveSource> sources, ArchiveRules? rules = null) =>
        ArchiveChapterValidator.Validate(
            new ArchiveChapterDraft(_docId, "bazaar", "2026-11", "Bazaar 2026-11", sentences, PredecessorPath: null),
            sources,
            rules ?? _defaults);

    private static ArchiveSource Agent(string id, string text, int minute, string kind = "gc_message") =>
        Source(id, text, minute, ArchiveTextClass.Agent, ContentTrust.Agent, MemoryTier.AgentVerified, kind);

    private static ArchiveSource Human(string id, string text, int minute, bool gcOptedIn) =>
        Source(id, text, minute, ArchiveTextClass.HumanText, ContentTrust.MemberHuman, MemoryTier.AgentVerified) with { GcOptedIn = gcOptedIn };

    private static ArchiveSource Reason(string id, string text, int minute, bool revealed) =>
        Source(id, text, minute, ArchiveTextClass.CouncilReason, ContentTrust.Agent, MemoryTier.AgentVerified) with { JointRevealDone = revealed };

    private static ArchiveSource Source(
        string id,
        string text,
        int minute,
        ArchiveTextClass textClass,
        ContentTrust origin,
        MemoryTier trust = MemoryTier.AgentVerified,
        string kind = "gc_message") =>
        new(
            Ref: kind + ":" + id,
            Kind: kind == "gc_event" ? ArchiveSourceKind.GcEvent : ArchiveSourceKind.GcMessage,
            TextClass: textClass,
            GcOptedIn: false,
            JointRevealDone: false,
            At: new DateTimeOffset(2026, 11, 1, 0, minute, 0, TimeSpan.Zero),
            Text: text,
            Trust: trust,
            OriginKind: origin);
}
