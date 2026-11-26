using System.Globalization;
using System.Text;
using Bazaar.Contracts.Memory;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Tools;

/// <summary>Maps wire strings to domain values and domain notes to wire DTOs. Pure.</summary>
internal static class MemoryToolMapping
{
    /// <summary>Scope names as they appear on the wire.</summary>
    public static string ScopeName(MemoryScope scope) => scope.ToString().ToLowerInvariant();

    /// <summary>Parses a wire scope. Hive is parsed too; callers decide whether it is allowed for their operation.</summary>
    public static bool TryParseScope(string text, out MemoryScope scope)
    {
        foreach (var candidate in Enum.GetValues<MemoryScope>())
        {
            if (string.Equals(ScopeName(candidate), text, StringComparison.Ordinal))
            {
                scope = candidate;
                return true;
            }
        }

        scope = default;
        return false;
    }

    /// <summary>Parses a kind filter. Null or empty means every kind.</summary>
    public static bool TryParseKinds(IReadOnlyList<string>? kinds, out IReadOnlySet<MemoryKind>? parsed)
    {
        if (kinds is null || kinds.Count == 0)
        {
            parsed = null;
            return true;
        }

        var set = new HashSet<MemoryKind>();
        foreach (var text in kinds)
        {
            if (!MemoryNoteWire.TryParseKind(text, out var kind))
            {
                parsed = null;
                return false;
            }

            set.Add(kind);
        }

        parsed = set;
        return true;
    }

    /// <summary>Position of a provenance in the trust order, most trusted first (owner, system, member-human, agent, external, quarantined).</summary>
    public static int Rank(ContentTrust trust) => trust switch
    {
        ContentTrust.Owner => 0,
        ContentTrust.System => 1,
        ContentTrust.MemberHuman => 2,
        ContentTrust.Agent => 3,
        ContentTrust.External => 4,
        ContentTrust.Quarantined => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, "Unknown source trust."),
    };

    /// <summary>The less trusted of two provenances.</summary>
    public static ContentTrust Worse(ContentTrust a, ContentTrust b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>
    /// The excerpt returned for a note. T1 bodies are quoted line by line and labelled as data, so they never read
    /// as instructions (docs/design/08-memory-system.md, section 5).
    /// </summary>
    public static (string Excerpt, bool Labelled) Excerpt(MemoryTier tier, string body)
    {
        var trimmed = body.Trim();
        if (MemoryTrustRules.RetrievalFor(tier) != MemoryRetrieval.LabelledQuotedData)
        {
            return (trimmed, false);
        }

        var quoted = new StringBuilder();
        foreach (var line in trimmed.Split('\n'))
        {
            if (quoted.Length > 0)
            {
                quoted.Append('\n');
            }

            quoted.Append("> ").Append(line.TrimEnd('\r'));
        }

        return (quoted.ToString(), true);
    }

    /// <summary>Token estimate for budget packing: one token per four characters, rounded up. Deterministic.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>The wire DTO for a retrieved note.</summary>
    public static MemoryHitDto ToHit(MemoryNote note, MemoryScope scope, string excerpt, bool labelled) => new(
        NoteId: note.Id,
        Title: note.Title,
        Scope: ScopeName(scope),
        Tier: MemoryNoteWire.TierName(note.Trust),
        Kind: MemoryNoteWire.KindName(note.Kind),
        Origin: MemoryNoteWire.SourceTrustName(note.SourceTrust),
        BodyExcerpt: excerpt,
        Labelled: labelled);

    /// <summary>
    /// The vault path of a new note: episodic notes sit in a date folder under their ULID; other kinds use a slug of
    /// the title plus the first eight characters of the id (lowercased), so file names stay slugs.
    /// </summary>
    public static string NotePath(MemoryScope scope, MemoryKind kind, string owner, string id, string title, DateOnly day)
    {
        var folder = MemoryAreas.FolderFor(kind)!;
        var root = scope switch
        {
            MemoryScope.Private => "agents/" + owner + "/memory/",
            MemoryScope.Project => "projects/" + owner + "/memory/",
            _ => "company/memory/",
        };

        if (kind == MemoryKind.Episodic)
        {
            return root + folder + "/" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/" + id + ".md";
        }

        return root + folder + "/" + Slug(title) + "-" + id[^8..].ToLowerInvariant() + ".md";
    }

    private static string Slug(string title)
    {
        var sb = new StringBuilder();
        var dash = false;
        foreach (var c in title.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }

            if (sb.Length >= 40)
            {
                break;
            }
        }

        var slug = sb.ToString().TrimEnd('-');
        return slug.Length == 0 ? "note" : slug;
    }
}
