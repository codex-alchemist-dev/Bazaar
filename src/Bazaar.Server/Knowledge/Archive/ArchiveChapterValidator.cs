using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Knowledge.Archive;

/// <summary>
/// Keeps only the chapter sentences the Archive may publish (doc 25 Bazaar Archive, "Trust and crossing rules", settings
/// floors). Pure: no SQL, no clock, no I/O. A sentence without a valid source is dropped, never repaired.
/// </summary>
/// <remarks>
/// Floors enforced here, whatever the settings say: thinking-GC text is always dropped; council reasons before the joint
/// reveal are always dropped; human text needs both the per-GC opt-in and <c>archive.include_human_text</c>.
/// The output depends only on the draft's sentence order and the set of sources, not on source input order.
/// </remarks>
public static class ArchiveChapterValidator
{
    /// <summary>Validates a draft against the source set and the settings rules.</summary>
    /// <exception cref="ArgumentException">Two sources share a reference.</exception>
    public static ValidatedArchiveChapter Validate(ArchiveChapterDraft draft, IEnumerable<ArchiveSource> sources, ArchiveRules rules)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(rules);
        var index = ArchiveSourceIndex.Build(sources);

        var accepted = new List<ArchiveSentence>();
        var dropped = new Dictionary<ArchiveDropReason, int>();
        var acceptedSources = new List<ArchiveSource>();

        foreach (var sentence in draft.Sentences)
        {
            var (reason, source) = Classify(sentence, index, rules);
            if (reason is null)
            {
                accepted.Add(sentence);
                acceptedSources.Add(source!);
                continue;
            }

            dropped[reason.Value] = dropped.GetValueOrDefault(reason.Value) + 1;
        }

        // The highest-trust source sets the chapter's trust; ties break on origin so the result is deterministic.
        var lead = acceptedSources
            .OrderByDescending(s => s.Trust)
            .ThenBy(s => s.OriginKind)
            .FirstOrDefault();

        return new ValidatedArchiveChapter(
            DocId: draft.DocId,
            Project: draft.Project,
            Slug: draft.Slug,
            Title: draft.Title,
            PredecessorPath: draft.PredecessorPath,
            Sentences: accepted,
            Trust: lead?.Trust ?? MemoryTier.Quarantined,
            OriginKind: lead?.OriginKind ?? ContentTrust.System,
            Dropped: dropped);
    }

    private static (ArchiveDropReason? Reason, ArchiveSource? Source) Classify(
        ArchiveSentence sentence,
        IReadOnlyDictionary<string, ArchiveSource> index,
        ArchiveRules rules)
    {
        if (string.IsNullOrWhiteSpace(sentence.SourceRef))
        {
            return (ArchiveDropReason.MissingRef, null);
        }

        if (!index.TryGetValue(sentence.SourceRef, out var source))
        {
            return (ArchiveDropReason.UnknownSource, null);
        }

        var sourceReason = SourceDropReason(source, rules);
        if (sourceReason is not null)
        {
            return (sourceReason, null);
        }

        if (string.IsNullOrWhiteSpace(sentence.Text))
        {
            return (ArchiveDropReason.EmptyText, null);
        }

        if (sentence.Text.IndexOfAny(['\r', '\n']) >= 0 || sentence.Text.Contains("-->", StringComparison.Ordinal))
        {
            return (ArchiveDropReason.UnsafeText, null);
        }

        return (null, source);
    }

    private static ArchiveDropReason? SourceDropReason(ArchiveSource source, ArchiveRules rules) => source.TextClass switch
    {
        ArchiveTextClass.Thinking => ArchiveDropReason.ThinkingGc,
        ArchiveTextClass.HumanText when !(rules.IncludeHumanText && source.GcOptedIn) => ArchiveDropReason.HumanText,
        ArchiveTextClass.CouncilReason when !source.JointRevealDone => ArchiveDropReason.CouncilReasonBeforeReveal,
        ArchiveTextClass.CouncilReason when !rules.IncludeCouncilReasons => ArchiveDropReason.CouncilReasonExcluded,
        _ => null,
    };
}

/// <summary>Indexes sources by reference. Shared by the validator and the generator so both reject the same inputs.</summary>
internal static class ArchiveSourceIndex
{
    public static IReadOnlyDictionary<string, ArchiveSource> Build(IEnumerable<ArchiveSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var index = new Dictionary<string, ArchiveSource>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!index.TryAdd(source.Ref, source))
            {
                throw new ArgumentException($"Two archive sources share the reference '{source.Ref}'.", nameof(sources));
            }
        }

        return index;
    }
}
