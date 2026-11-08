namespace Bazaar.Server.Knowledge.Twin;

/// <summary>
/// One derived project fact (doc 25 <c>twin_facts</c>). Twin facts are a rebuildable index over sources and are never authoritative:
/// the same sources always produce the same facts with the same keys. Every fact names at least one <see cref="TwinSourceRef"/>, so a
/// reader can trace it back to the input it came from.
/// </summary>
/// <param name="ProjectId">The project the fact is about.</param>
/// <param name="Kind">One of <see cref="TwinFactKinds"/>.</param>
/// <param name="Subject">What the fact is about, such as a package id or a PR node id. Stable across rebuilds.</param>
/// <param name="Value">The fact's value, such as a version or a state.</param>
/// <param name="Confidence">0 to 1. Direct reads of one source are 1.0; conflicting sources lower it later (doc 25).</param>
/// <param name="Status">Active until a source change marks it stale.</param>
/// <param name="SourceRefs">The inputs the fact was read from. Never empty.</param>
/// <param name="ObservedAt">When the source itself says the thing happened, taken from the source (never from the clock). Null when the source has no time, such as a repo file at a commit.</param>
/// <param name="ExtractedAt">When the extractor produced the fact, from the injected <see cref="TimeProvider"/>.</param>
public sealed record TwinFact(
    string ProjectId,
    string Kind,
    string Subject,
    string Value,
    double Confidence,
    TwinFactStatus Status,
    IReadOnlyList<TwinSourceRef> SourceRefs,
    DateTimeOffset? ObservedAt,
    DateTimeOffset ExtractedAt)
{
    /// <summary>The identity key. Two facts with the same key are the same fact; the newer observation replaces the older.</summary>
    public string Key => $"{ProjectId}\u001f{Kind}\u001f{Subject}";
}

/// <summary>A pointer to one input of a fact. <see cref="Kind"/> is one of <see cref="TwinSourceKinds"/>.</summary>
/// <param name="Kind">The type of source.</param>
/// <param name="Ref">The fixed reference inside that source: a repo path, a GitHub event id, or a CI run id.</param>
/// <param name="Commit">The commit the source was read at, when the source has one.</param>
public sealed record TwinSourceRef(string Kind, string Ref, string? Commit = null);

/// <summary>Fact kinds written by the Twin extractors in this slice of doc 25 WP3.</summary>
public static class TwinFactKinds
{
    /// <summary>A package version pinned in a repo's central package file. Subject is the package id, value the version as written.</summary>
    public const string Dependency = "dependency";

    /// <summary>The state of a pull request. Subject is the PR node id; value is <c>open</c>, <c>merged</c> or <c>closed</c>.</summary>
    public const string PullRequestState = "pr-state";

    /// <summary>The result of a CI run. Subject is the run id; value is <c>passed</c> or <c>failed</c>.</summary>
    public const string CiRun = "ci-run";
}

/// <summary>Source kinds a <see cref="TwinSourceRef"/> can name.</summary>
public static class TwinSourceKinds
{
    /// <summary>A text file at a commit in a repo, read through <c>IGitProvider</c>. Ref is the repo path.</summary>
    public const string RepoFile = "repo-file";

    /// <summary>A forge event (doc 21). Ref is the event id.</summary>
    public const string ForgeEvent = "forge-event";

    /// <summary>A completed CI run (doc 21). Ref is the run id.</summary>
    public const string CiRun = "ci-run";
}

/// <summary>Lifecycle of a Twin fact.</summary>
public enum TwinFactStatus
{
    /// <summary>The fact matches its sources as of the last rebuild.</summary>
    Active,

    /// <summary>A source changed after the fact was written. The fact stays until it is rebuilt.</summary>
    Stale,
}
