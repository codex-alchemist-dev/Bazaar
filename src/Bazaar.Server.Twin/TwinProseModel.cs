using Bazaar.Server.Knowledge.Twin;

namespace Bazaar.Server.Twin;

/// <summary>
/// Input for one Twin prose page (doc 25 WP4). The facts are the only material the model sees and the only things a sentence may cite.
/// </summary>
/// <param name="Project">The project the page is about. A path segment: lowercase letters, digits and single hyphens.</param>
/// <param name="Page">The page name, unique within the project. A path segment with the same rules as <paramref name="Project"/>.</param>
/// <param name="Title">The page title. Front matter forbids # | ^ : %% [[ ]] in it.</param>
/// <param name="Facts">The facts the page may summarise. Never empty.</param>
public sealed record TwinProseRequest(string Project, string Page, string Title, IReadOnlyList<TwinFact> Facts);

/// <summary>One sentence of a draft. <see cref="SourceRef"/> is null when the model cited nothing.</summary>
public sealed record TwinProseSentence(string Text, string? SourceRef);

/// <summary>Why the validator dropped a sentence.</summary>
public enum TwinDropReason
{
    /// <summary>The sentence cites nothing, or a blank reference.</summary>
    MissingRef,

    /// <summary>The cited reference is not one of the supplied fact ids or fact source refs.</summary>
    UnknownRef,

    /// <summary>Blank sentence text.</summary>
    EmptyText,

    /// <summary>Text or a reference that would break the page's markup (a line break or a comment marker).</summary>
    UnsafeText,
}

/// <summary>A page after validation: only sentences with a valid source remain.</summary>
/// <param name="Sentences">The accepted sentences in draft order.</param>
/// <param name="Dropped">Dropped sentence counts by reason. Only reasons that occurred are present.</param>
public sealed record ValidatedTwinPage(
    string Project,
    string Page,
    string Title,
    IReadOnlyList<TwinProseSentence> Sentences,
    IReadOnlyDictionary<TwinDropReason, int> Dropped)
{
    /// <summary>The total number of dropped sentences.</summary>
    public int DroppedCount => Dropped.Values.Sum();
}

/// <summary>Builds source references in one place, so the prompt, the validator and the page agree on the form.</summary>
public static class TwinRefs
{
    /// <summary>The reference a sentence cites for a fact: <c>twin_fact:&lt;kind&gt;:&lt;subject&gt;</c>.</summary>
    public static string ForFact(TwinFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return $"twin_fact:{fact.Kind}:{fact.Subject}";
    }

    /// <summary>The reference a sentence may cite for a fact's own input: <c>&lt;source kind&gt;:&lt;ref&gt;</c>, with <c>@commit</c> when known.</summary>
    public static string ForSource(TwinSourceRef source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Commit is null ? $"{source.Kind}:{source.Ref}" : $"{source.Kind}:{source.Ref}@{source.Commit}";
    }

    /// <summary>Every reference a request's facts allow, ordered ordinally.</summary>
    public static IReadOnlyList<string> Allowed(IEnumerable<TwinFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var refs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            refs.Add(ForFact(fact));
            foreach (var source in fact.SourceRefs)
            {
                refs.Add(ForSource(source));
            }
        }

        return [.. refs];
    }
}
