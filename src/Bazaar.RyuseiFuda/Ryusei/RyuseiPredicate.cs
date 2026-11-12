using System.Text.RegularExpressions;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// The closed v1 predicate set (doc 38, Trigger types). Each kind names the event kind it reads. Wire names are the
/// strings stored in a spec; they are compared exactly, so a variant spelling is an unknown kind.
/// </summary>
public enum RyuseiPredicateKind
{
    TaskTransitioned,
    TaskDuePassed,
    TaskBlocked,
    TaskUnblocked,
    PrMerged,
    PrClosed,
    PrReviewRequested,
    GcMessage,
    VaultChanged,
    CouncilDecisionRecorded,
}

/// <summary>
/// A predicate as written in a spec. <see cref="Kind"/> is the wire name (for example <c>task.transitioned</c>), so an
/// unvalidated spec can hold any string. Use <see cref="RyuseiPredicateValidator"/> before storing one.
/// </summary>
public sealed record RyuseiPredicate
{
    /// <summary>The wire name of the predicate kind.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Optional. For <c>gc.message</c>, only messages in this GC match.</summary>
    public Guid? GcId { get; init; }

    /// <summary>Optional. For <c>gc.message</c>, only messages from this author match.</summary>
    public Guid? AuthorAgentId { get; init; }

    /// <summary>Optional. For <c>vault.changed</c>, a path glob (<c>*</c>, <c>**</c>, <c>?</c>) the changed path must match.</summary>
    public string? PathGlob { get; init; }
}

/// <summary>One event from the stream a watcher or event trigger reads. <see cref="Kind"/> is the wire name.</summary>
public sealed record RyuseiEvent
{
    public string Kind { get; init; } = string.Empty;
    public Guid? GcId { get; init; }
    public Guid? AuthorAgentId { get; init; }

    /// <summary>The Vault path of a changed document, for <c>vault.changed</c>.</summary>
    public string? VaultPath { get; init; }
}

/// <summary>Wire names and parsing for <see cref="RyuseiPredicateKind"/>.</summary>
public static class RyuseiPredicates
{
    private static readonly Dictionary<RyuseiPredicateKind, string> _wireNames = new Dictionary<RyuseiPredicateKind, string>
    {
        [RyuseiPredicateKind.TaskTransitioned] = "task.transitioned",
        [RyuseiPredicateKind.TaskDuePassed] = "task.due_passed",
        [RyuseiPredicateKind.TaskBlocked] = "task.blocked",
        [RyuseiPredicateKind.TaskUnblocked] = "task.unblocked",
        [RyuseiPredicateKind.PrMerged] = "pr.merged",
        [RyuseiPredicateKind.PrClosed] = "pr.closed",
        [RyuseiPredicateKind.PrReviewRequested] = "pr.review_requested",
        [RyuseiPredicateKind.GcMessage] = "gc.message",
        [RyuseiPredicateKind.VaultChanged] = "vault.changed",
        [RyuseiPredicateKind.CouncilDecisionRecorded] = "council.decision_recorded",
    };

    /// <summary>The wire name of a kind, for example <c>task.due_passed</c>.</summary>
    public static string WireName(RyuseiPredicateKind kind) => _wireNames[kind];

    /// <summary>Parses a wire name exactly. Returns false for anything outside the closed set.</summary>
    public static bool TryParse(string? wireName, out RyuseiPredicateKind kind)
    {
        foreach (var (candidate, name) in _wireNames)
        {
            if (string.Equals(name, wireName, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>Whether a predicate matches an event (doc 38, closed predicate set). Pure; no I/O.</summary>
public static class RyuseiPredicateMatcher
{
    /// <summary>
    /// True when the event has the predicate's wire name and passes its filters. An unknown predicate kind never matches.
    /// Filters that the predicate does not declare are ignored here; the validator refuses them at creation.
    /// </summary>
    public static bool Matches(RyuseiPredicate predicate, RyuseiEvent @event)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(@event);

        if (!RyuseiPredicates.TryParse(predicate.Kind, out var kind))
        {
            return false;
        }

        if (!string.Equals(@event.Kind, RyuseiPredicates.WireName(kind), StringComparison.Ordinal))
        {
            return false;
        }

        return kind switch
        {
            RyuseiPredicateKind.GcMessage =>
                (predicate.GcId is null || predicate.GcId == @event.GcId)
                && (predicate.AuthorAgentId is null || predicate.AuthorAgentId == @event.AuthorAgentId),

            RyuseiPredicateKind.VaultChanged =>
                predicate.PathGlob is null
                || (@event.VaultPath is not null && RyuseiPathGlob.IsMatch(predicate.PathGlob, @event.VaultPath)),

            _ => true,
        };
    }
}

/// <summary>
/// Path globs for <c>vault.changed</c>. <c>*</c> matches within one path segment, <c>**</c> matches across segments,
/// <c>**/</c> may match no segments, and <c>?</c> matches one non-separator character. Paths use <c>/</c>.
/// </summary>
public static class RyuseiPathGlob
{
    /// <summary>Whether <paramref name="path"/> matches <paramref name="glob"/> as a whole string.</summary>
    public static bool IsMatch(string glob, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(glob);
        ArgumentNullException.ThrowIfNull(path);

        return Compile(glob).IsMatch(path);
    }

    /// <summary>Translates a glob to an anchored regular expression. Literal characters are escaped.</summary>
    internal static Regex Compile(string glob)
    {
        var pattern = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    // "**/" matches zero or more whole segments.
                    i++;
                    pattern.Append("(?:.*/)?");
                }
                else
                {
                    pattern.Append(".*");
                }
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    }
}
