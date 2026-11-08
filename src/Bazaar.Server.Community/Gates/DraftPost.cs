namespace Bazaar.Server.Community.Gates;

/// <summary>
/// A community post as drafted by an agent, before sponsor approval (doc 36, "Quality gates before any Tier 2 post").
/// Pure data; the gate checks in <see cref="QualityGates"/> read it and do no I/O.
/// </summary>
/// <param name="Platform">Platform key, matching <see cref="PlatformPolicy.Platform"/>.</param>
/// <param name="Kind">Item kind: "issue", "pr", "thread" or "comment".</param>
/// <param name="Body">Exact outbound text, including the disclosure footer. Checked for personal data, secrets and internal links.</param>
/// <param name="DuplicateSearchQueries">Search queries the agent ran on the platform before drafting.</param>
/// <param name="RelatedLinks">Existing threads or issues the draft relates to.</param>
/// <param name="ExampleOutput">Actual output of the minimal example the agent ran in its VM (bug reports only).</param>
/// <param name="ExampleVersions">Versions of the tools that produced <paramref name="ExampleOutput"/> (bug reports only).</param>
/// <param name="IsBugReport">True when the draft reports a bug and therefore needs example evidence.</param>
/// <param name="DuplicateSearchLinksChecked">Links to existing threads the duplicate search actually opened and reviewed.</param>
public sealed record DraftPost(
    string Platform,
    string Kind,
    string Body,
    IReadOnlyList<string> DuplicateSearchQueries,
    IReadOnlyList<string> RelatedLinks,
    string? ExampleOutput,
    string? ExampleVersions,
    bool IsBugReport,
    IReadOnlyList<string> DuplicateSearchLinksChecked);
