using System.Text;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Vault.Writes;

namespace Bazaar.Server.Knowledge.Archive;

/// <summary>
/// Renders a validated chapter as Vault Markdown (doc 25 Bazaar Archive). Pure. Front matter carries <c>trust</c> (doc 08
/// tier, T0 to T3), <c>origin_kind</c> (doc 15 provenance) and, for a regenerated chapter, <c>predecessor</c>. Each sentence
/// is one paragraph followed by an HTML comment that names its source, which the reader view turns into a link.
/// </summary>
public static class ArchiveChapterRenderer
{
    public const string ArchiveType = "archive";

    public static string Render(ValidatedArchiveChapter chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("id: ").Append(VaultWriter.ToUlid(chapter.DocId)).Append('\n');
        builder.Append("type: ").Append(ArchiveType).Append('\n');
        builder.Append("title: ").Append(chapter.Title).Append('\n');
        builder.Append("project: ").Append(chapter.Project).Append('\n');
        builder.Append("trust: ").Append(TierLabel(chapter.Trust)).Append('\n');
        builder.Append("origin_kind: ").Append(OriginLabel(chapter.OriginKind)).Append('\n');
        if (chapter.PredecessorPath is not null)
        {
            builder.Append("predecessor: ").Append(chapter.PredecessorPath).Append('\n');
        }

        builder.Append("---\n\n");
        builder.Append("# ").Append(chapter.Title).Append("\n\n");
        foreach (var sentence in chapter.Sentences)
        {
            builder.Append(sentence.Text).Append(" <!-- source: ").Append(sentence.SourceRef).Append(" -->\n\n");
        }

        return builder.ToString();
    }

    /// <summary>The doc 08 tier as the label front matter uses.</summary>
    internal static string TierLabel(MemoryTier tier) => tier switch
    {
        MemoryTier.Quarantined => "T0",
        MemoryTier.UntrustedDerived => "T1",
        MemoryTier.AgentVerified => "T2",
        MemoryTier.OwnerPromoted => "T3",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown memory tier."),
    };

    /// <summary>The doc 15 provenance value as the label front matter uses.</summary>
    internal static string OriginLabel(ContentTrust origin) => origin switch
    {
        ContentTrust.Owner => "owner",
        ContentTrust.System => "system",
        ContentTrust.MemberHuman => "member-human",
        ContentTrust.Agent => "agent",
        ContentTrust.External => "external",
        ContentTrust.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown provenance."),
    };
}
