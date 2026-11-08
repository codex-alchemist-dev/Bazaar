using System.Xml;
using System.Xml.Linq;
using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Knowledge.Twin;

/// <summary>
/// A pull request event as the Twin receives it. The forge adapter (doc 21) maps its own event type into this record, so the Twin
/// does not depend on the GitHub assembly. <paramref name="Action"/> is the forge action such as <c>opened</c> or <c>closed</c>.
/// </summary>
/// <param name="EventId">The forge's event id. Used as the source reference.</param>
/// <param name="PrNodeId">The PR's stable node id. Used as the fact subject.</param>
/// <param name="Action">The forge action name.</param>
/// <param name="Merged">True when a <c>closed</c> PR was merged rather than closed.</param>
/// <param name="At">When the event happened, UTC.</param>
/// <param name="HeadSha">The head commit at the event, when known.</param>
public sealed record PullRequestEventFact(string EventId, string PrNodeId, string Action, bool Merged, DateTimeOffset At, string? HeadSha = null);

/// <summary>
/// Deterministic extractors that turn already-loaded source facts into <see cref="TwinFact"/>s. No model, no network and no storage.
/// The only clock is the injected <see cref="TimeProvider"/>, used to stamp <see cref="TwinFact.ExtractedAt"/>. Output is sorted by
/// <see cref="TwinFact.Key"/>, so the same inputs always give the same facts in the same order.
/// </summary>
public sealed class TwinFactExtractor(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Reads <c>PackageVersion</c> items from a central package file (for example <c>Directory.Packages.props</c>) at a commit.
    /// A package named twice with different versions keeps the first occurrence and drops to confidence 0.5, because the sources
    /// conflict and the Twin never overwrites silently. Malformed XML throws <see cref="XmlException"/>; the caller records a gap.
    /// </summary>
    public IReadOnlyList<TwinFact> ExtractDependencies(string projectId, string path, string commitSha, string packagePropsXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        ArgumentNullException.ThrowIfNull(packagePropsXml);

        var document = XDocument.Parse(packagePropsXml);
        var extractedAt = _time.GetUtcNow();
        var source = new TwinSourceRef(TwinSourceKinds.RepoFile, path, commitSha);

        var byPackage = new Dictionary<string, (string Version, bool Conflict)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Descendants("PackageVersion"))
        {
            var package = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update");
            var version = (string?)item.Attribute("Version");
            if (string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(version))
                continue;

            package = package.Trim();
            version = version.Trim();
            if (!byPackage.TryGetValue(package, out var seen))
            {
                byPackage[package] = (version, false);
            }
            else if (!string.Equals(seen.Version, version, StringComparison.Ordinal))
            {
                byPackage[package] = (seen.Version, true);
            }
        }

        return byPackage
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new TwinFact(
                projectId,
                TwinFactKinds.Dependency,
                entry.Key,
                entry.Value.Version,
                entry.Value.Conflict ? 0.5 : 1.0,
                TwinFactStatus.Active,
                [source],
                ObservedAt: null,
                extractedAt))
            .ToList();
    }

    /// <summary>
    /// Derives the state of each pull request from its events. The newest event per PR wins (by <see cref="PullRequestEventFact.At"/>,
    /// then by event id), so replayed or out-of-order events give the same result. Actions that do not change the state are ignored.
    /// </summary>
    public IReadOnlyList<TwinFact> ExtractPullRequestStates(string projectId, IEnumerable<PullRequestEventFact> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(events);

        var extractedAt = _time.GetUtcNow();
        var latest = new Dictionary<string, (PullRequestEventFact Event, string State)>(StringComparer.Ordinal);
        foreach (var evt in events)
        {
            var state = StateOf(evt);
            if (state is null || string.IsNullOrWhiteSpace(evt.PrNodeId))
                continue;

            if (latest.TryGetValue(evt.PrNodeId, out var current) && !IsNewer(evt, current.Event))
                continue;

            latest[evt.PrNodeId] = (evt, state);
        }

        return latest
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new TwinFact(
                projectId,
                TwinFactKinds.PullRequestState,
                entry.Key,
                entry.Value.State,
                1.0,
                TwinFactStatus.Active,
                [new TwinSourceRef(TwinSourceKinds.ForgeEvent, entry.Value.Event.EventId, entry.Value.Event.HeadSha)],
                entry.Value.Event.At,
                extractedAt))
            .ToList();
    }

    /// <summary>
    /// One fact per CI run id. A run id that repeats keeps the latest completion. Runs that did not finish are not passed in, so a run
    /// still in progress produces no fact.
    /// </summary>
    public IReadOnlyList<TwinFact> ExtractCiRuns(string projectId, IEnumerable<CiRunFact> runs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(runs);

        var extractedAt = _time.GetUtcNow();
        var latest = new Dictionary<string, CiRunFact>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            if (string.IsNullOrWhiteSpace(run.RunId))
                continue;

            if (!latest.TryGetValue(run.RunId, out var current) || run.CompletedAt > current.CompletedAt)
                latest[run.RunId] = run;
        }

        return latest.Values
            .OrderBy(run => run.RunId, StringComparer.Ordinal)
            .Select(run => new TwinFact(
                projectId,
                TwinFactKinds.CiRun,
                run.RunId,
                run.Passed ? "passed" : "failed",
                1.0,
                TwinFactStatus.Active,
                [new TwinSourceRef(TwinSourceKinds.CiRun, run.RunId)],
                run.CompletedAt,
                extractedAt))
            .ToList();
    }

    private static string? StateOf(PullRequestEventFact evt) => evt.Action switch
    {
        "opened" or "reopened" or "ready_for_review" or "converted_to_draft" => "open",
        "closed" => evt.Merged ? "merged" : "closed",
        _ => null,
    };

    private static bool IsNewer(PullRequestEventFact candidate, PullRequestEventFact current)
    {
        if (candidate.At != current.At)
            return candidate.At > current.At;

        return string.CompareOrdinal(candidate.EventId, current.EventId) > 0;
    }
}
