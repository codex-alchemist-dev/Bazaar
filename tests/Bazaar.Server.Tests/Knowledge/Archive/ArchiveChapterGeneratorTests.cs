using Bazaar.Server.Knowledge.Archive;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Knowledge.Archive;

public sealed class ArchiveChapterGeneratorTests
{
    private static readonly Guid _docId = Guid.Parse("0196a7c4-0000-7000-8000-0000000000b1");
    private static readonly DateTimeOffset _start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _end = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Generator_writes_one_sentence_per_source_in_the_window_with_its_ref()
    {
        var draft = Generate(Sources());

        Assert.Equal(["Alpha said.", "Beta decided.", "Gamma replied."], draft.Sentences.Select(s => s.Text));
        Assert.Equal(
            [ArchiveSourceRef.GcMessage("a"), ArchiveSourceRef.GcEvent("b"), ArchiveSourceRef.Decision("decisions/0001-x.md")],
            draft.Sentences.Select(s => s.SourceRef));
    }

    [Fact]
    public void Sources_outside_the_window_are_not_in_the_draft()
    {
        var sources = Sources().Append(Source("late", "Too late.", new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero)));

        var draft = Generate(sources);

        Assert.DoesNotContain(draft.Sentences, s => s.Text == "Too late.");
    }

    [Fact]
    public void Shuffled_input_gives_the_same_draft_and_the_same_validated_chapter()
    {
        var sources = Sources().ToList();
        var forward = Generate(sources);
        var reversed = Generate(Enumerable.Reverse(sources).ToList());
        var rotated = Generate(sources.Skip(1).Append(sources[0]).ToList());

        Assert.Equal(forward.Sentences, reversed.Sentences);
        Assert.Equal(forward.Sentences, rotated.Sentences);

        var rules = ArchiveRules.Defaults;
        var validatedForward = ArchiveChapterValidator.Validate(forward, sources, rules);
        var validatedReversed = ArchiveChapterValidator.Validate(forward, Enumerable.Reverse(sources).ToList(), rules);
        Assert.Equal(validatedForward.Sentences, validatedReversed.Sentences);
        Assert.Equal(validatedForward.Trust, validatedReversed.Trust);
        Assert.Equal(validatedForward.OriginKind, validatedReversed.OriginKind);
    }

    [Fact]
    public void Generated_draft_passes_the_validator_without_drops_for_agent_sources()
    {
        var sources = Sources();

        var validated = ArchiveChapterValidator.Validate(Generate(sources), sources, ArchiveRules.Defaults);

        Assert.Equal(3, validated.Sentences.Count);
        Assert.Empty(validated.Dropped);
    }

    [Fact]
    public void Regenerated_chapter_records_its_predecessor_in_front_matter()
    {
        var draft = ArchiveChapterGenerator.Generate(new ArchiveChapterRequest(
            _docId, "bazaar", "2026-12-r2", "Bazaar 2026-12 r2", _start, _end, Sources(), PredecessorPath: "archive/bazaar/2026-12.md"));
        var validated = ArchiveChapterValidator.Validate(draft, Sources(), ArchiveRules.Defaults);

        var markdown = ArchiveChapterRenderer.Render(validated);

        Assert.Equal("archive/bazaar/2026-12.md", validated.PredecessorPath);
        Assert.Contains("type: archive\n", markdown);
        Assert.Contains("predecessor: archive/bazaar/2026-12.md\n", markdown);
        Assert.Contains("trust: T2\n", markdown);
        Assert.Contains("origin_kind: agent\n", markdown);
    }

    [Fact]
    public void First_chapter_has_no_predecessor_line()
    {
        var validated = ArchiveChapterValidator.Validate(Generate(Sources()), Sources(), ArchiveRules.Defaults);

        Assert.DoesNotContain("predecessor:", ArchiveChapterRenderer.Render(validated));
    }

    [Fact]
    public void Chapter_cannot_be_its_own_predecessor()
    {
        Assert.Throws<ArgumentException>(() => ArchiveChapterGenerator.Generate(new ArchiveChapterRequest(
            _docId, "bazaar", "2026-11", "Bazaar 2026-11", _start, _end, Sources(), PredecessorPath: "archive/bazaar/2026-11.md")));
    }

    [Fact]
    public void Rendered_sentences_cite_their_source_in_a_comment()
    {
        var validated = ArchiveChapterValidator.Validate(Generate(Sources()), Sources(), ArchiveRules.Defaults);

        var markdown = ArchiveChapterRenderer.Render(validated);

        Assert.Contains("Alpha said. <!-- source: gc_message:a -->", markdown);
        Assert.Contains("# Bazaar 2026-11", markdown);
    }

    [Fact]
    public void Project_and_slug_must_be_path_segments()
    {
        Assert.Throws<ArgumentException>(() => ArchiveChapterGenerator.Generate(new ArchiveChapterRequest(
            _docId, "Bazaar Project", "2026-11", "Title", _start, _end, [], null)));
        Assert.Throws<ArgumentException>(() => ArchiveChapterGenerator.Generate(new ArchiveChapterRequest(
            _docId, "bazaar", "../escape", "Title", _start, _end, [], null)));
    }

    [Fact]
    public void Titles_with_forbidden_front_matter_characters_are_refused()
    {
        Assert.Throws<ArgumentException>(() => ArchiveChapterGenerator.Generate(new ArchiveChapterRequest(
            _docId, "bazaar", "2026-11", "Bazaar: November", _start, _end, [], null)));
    }

    private static ArchiveChapterDraft Generate(IEnumerable<ArchiveSource> sources) => ArchiveChapterGenerator.Generate(new ArchiveChapterRequest(
        _docId, "bazaar", "2026-11", "Bazaar 2026-11", _start, _end, sources, PredecessorPath: null));

    private static List<ArchiveSource> Sources() =>
    [
        Source("a", "Alpha said.", new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero)),
        Source("b", "Beta decided.", new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), kind: ArchiveSourceKind.GcEvent),
        Source("0001-x", "Gamma replied.", new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), kind: ArchiveSourceKind.Decision,
            textClass: ArchiveTextClass.Decision),
    ];

    private static ArchiveSource Source(
        string id,
        string text,
        DateTimeOffset at,
        ArchiveSourceKind kind = ArchiveSourceKind.GcMessage,
        ArchiveTextClass textClass = ArchiveTextClass.Agent) =>
        new(
            Ref: kind switch
            {
                ArchiveSourceKind.GcEvent => ArchiveSourceRef.GcEvent(id),
                ArchiveSourceKind.Decision => ArchiveSourceRef.Decision("decisions/" + id + ".md"),
                _ => ArchiveSourceRef.GcMessage(id),
            },
            Kind: kind,
            TextClass: textClass,
            GcOptedIn: false,
            JointRevealDone: false,
            At: at,
            Text: text,
            Trust: MemoryTier.AgentVerified,
            OriginKind: ContentTrust.Agent);
}
