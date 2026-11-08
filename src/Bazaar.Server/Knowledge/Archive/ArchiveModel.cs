using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Knowledge.Archive;

/// <summary>The record types an Archive sentence may cite (doc 25 Bazaar Archive row).</summary>
public enum ArchiveSourceKind
{
    GcMessage,
    GcEvent,
    Decision,
}

/// <summary>
/// What a source text is, for the Archive's exclusion rules. <see cref="Thinking"/> is never archived; a
/// <see cref="HumanText"/> entry is archived only from an opted-in GC; a <see cref="CouncilReason"/> only after the joint reveal.
/// </summary>
public enum ArchiveTextClass
{
    Agent,
    HumanText,
    CouncilReason,
    Thinking,
    Decision,
}

/// <summary>
/// One citable source: a GC entry or a decision record, with the flags the exclusion rules read. <see cref="Ref"/> is the
/// key a sentence cites (see <see cref="ArchiveSourceRef"/>).
/// </summary>
/// <param name="Ref">The source reference, such as <c>gc_message:&lt;id&gt;</c>.</param>
/// <param name="Kind">Which record type the reference names.</param>
/// <param name="TextClass">The exclusion class of the text.</param>
/// <param name="GcOptedIn">For a human-text entry: whether its GC opted in to the Archive. Ignored for other classes.</param>
/// <param name="JointRevealDone">For a council reason: whether the vote's joint reveal has happened. Ignored for other classes.</param>
/// <param name="At">When the source was recorded. Sentences are ordered by it.</param>
/// <param name="Text">The source text, one line.</param>
/// <param name="Trust">The trust tier of the source (doc 08). Chapters take the highest tier of their accepted sources.</param>
/// <param name="OriginKind">The provenance of the source (doc 15).</param>
public sealed record ArchiveSource(
    string Ref,
    ArchiveSourceKind Kind,
    ArchiveTextClass TextClass,
    bool GcOptedIn,
    bool JointRevealDone,
    DateTimeOffset At,
    string Text,
    MemoryTier Trust,
    ContentTrust OriginKind);

/// <summary>Builds source references in one place, so writers and the validator agree on the form.</summary>
public static class ArchiveSourceRef
{
    public static string GcMessage(string id) => "gc_message:" + id;

    public static string GcEvent(string id) => "gc_event:" + id;

    public static string Decision(string vaultPath) => "decision:" + vaultPath;
}

/// <summary>The settings the exclusion rules read (doc 25 <c>archive.include_human_text</c>, <c>archive.include_council_reasons</c>).</summary>
/// <param name="IncludeHumanText">Archive human text from opted-in GCs. Default false.</param>
/// <param name="IncludeCouncilReasons">Archive council reasons after the joint reveal. Default true.</param>
public sealed record ArchiveRules(bool IncludeHumanText, bool IncludeCouncilReasons)
{
    /// <summary>The shipped defaults from doc 25.</summary>
    public static ArchiveRules Defaults { get; } = new(IncludeHumanText: false, IncludeCouncilReasons: true);
}

/// <summary>One sentence of a chapter. <see cref="SourceRef"/> is null when the sentence cites nothing.</summary>
public sealed record ArchiveSentence(string Text, string? SourceRef);

/// <summary>A chapter before validation. Every sentence should cite a source; the validator decides what stays.</summary>
/// <param name="DocId">The chapter's document id (front matter <c>id</c> and the per-document write queue).</param>
/// <param name="Project">The project the chapter belongs to. A path segment: lowercase letters, digits and hyphens.</param>
/// <param name="Slug">The chapter slug, unique within the project. A path segment with the same rules as <paramref name="Project"/>.</param>
/// <param name="Title">The front matter title.</param>
/// <param name="Sentences">The sentences in reading order.</param>
/// <param name="PredecessorPath">The Vault path of the chapter this one regenerates, or null for a first chapter.</param>
public sealed record ArchiveChapterDraft(
    Guid DocId,
    string Project,
    string Slug,
    string Title,
    IReadOnlyList<ArchiveSentence> Sentences,
    string? PredecessorPath);

/// <summary>Why a sentence was dropped.</summary>
public enum ArchiveDropReason
{
    /// <summary>The sentence cites no source, or cites a blank reference.</summary>
    MissingRef,

    /// <summary>The cited reference is not in the source set.</summary>
    UnknownSource,

    /// <summary>Human text from a GC that did not opt in, or while <c>archive.include_human_text</c> is off.</summary>
    HumanText,

    /// <summary>A council reason before the joint reveal. Never archived.</summary>
    CouncilReasonBeforeReveal,

    /// <summary>A council reason after the reveal, while <c>archive.include_council_reasons</c> is off.</summary>
    CouncilReasonExcluded,

    /// <summary>Thinking-GC text. Never archived or published.</summary>
    ThinkingGc,

    /// <summary>Blank sentence text.</summary>
    EmptyText,

    /// <summary>Text that would break the chapter's markup (a line break or the comment close marker).</summary>
    UnsafeText,
}

/// <summary>A chapter after validation: only sentences with an allowed source remain, and the drops are counted by reason.</summary>
/// <param name="Trust">The highest trust tier of the accepted sources. <see cref="MemoryTier.Quarantined"/> when nothing was accepted.</param>
/// <param name="OriginKind">The origin of the accepted source with the highest trust. <see cref="ContentTrust.System"/> when nothing was accepted.</param>
/// <param name="Dropped">Dropped sentence counts by reason. Only reasons that occurred are present.</param>
public sealed record ValidatedArchiveChapter(
    Guid DocId,
    string Project,
    string Slug,
    string Title,
    string? PredecessorPath,
    IReadOnlyList<ArchiveSentence> Sentences,
    MemoryTier Trust,
    ContentTrust OriginKind,
    IReadOnlyDictionary<ArchiveDropReason, int> Dropped);
