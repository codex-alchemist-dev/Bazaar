using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Roadmap;

/// <summary>Objective status (docs/design/38-ryusei-fuda.md, "Objectives are Vault documents").</summary>
public enum RoadmapStatus
{
    Planned,
    Active,
    Done,
    Dropped,
}

/// <summary>
/// A parsed objective document. <see cref="Phase"/> is the phase slug. <see cref="DependsOn"/> holds bare slugs (the
/// <c>[[ ]]</c> wrapper is removed), in the order written.
/// </summary>
public sealed record RoadmapObjective(string Phase, RoadmapStatus Status, string? Owner, ImmutableArray<string> DependsOn) : RoadmapDocument;

/// <summary>A parsed phase document. A phase has no phase, depends_on or owner field in the contract.</summary>
public sealed record RoadmapPhase(RoadmapStatus Status) : RoadmapDocument;

/// <summary>Base type of a parsed roadmap document.</summary>
public abstract record RoadmapDocument;

/// <summary>One contract violation. <see cref="Field"/> is the front-matter key at fault.</summary>
public sealed record RoadmapFrontMatterError(string Field, string Message);

/// <summary>
/// Result of parsing front matter. Exactly one of <see cref="Document"/> or a non-empty <see cref="Errors"/> applies.
/// </summary>
public sealed record RoadmapParseResult(RoadmapDocument? Document, ImmutableArray<RoadmapFrontMatterError> Errors)
{
    public bool IsValid => Errors.IsEmpty;
}

/// <summary>
/// The front-matter contract for objectives and phases (doc 38, "Objectives are Vault documents"). Pure: no I/O, no database.
/// The Vault indexer and the views read the same keys; this validator is the write-side check.
/// </summary>
public static partial class RoadmapFrontMatter
{
    public const string KindObjective = "objective";
    public const string KindPhase = "phase";

    /// <summary>A slug: lowercase letters, digits and hyphens.</summary>
    public static bool IsSlug(string? value) => value is not null && SlugPattern().IsMatch(value);

    /// <summary>Parses and validates one document's front matter. Reports every violation it finds, not only the first.</summary>
    public static RoadmapParseResult Parse(JsonObject frontMatter)
    {
        ArgumentNullException.ThrowIfNull(frontMatter);
        var errors = new List<RoadmapFrontMatterError>();

        var kind = ReadString(frontMatter, "kind", errors);
        var isObjective = kind == KindObjective;
        var isPhase = kind == KindPhase;
        if (kind is not null && !isObjective && !isPhase)
            errors.Add(new("kind", $"kind must be '{KindObjective}' or '{KindPhase}', not '{kind}'."));
        else if (kind is null && !IsPresent(frontMatter, "kind"))
            errors.Add(new("kind", "kind is required."));

        var status = ReadStatus(frontMatter, errors);

        string? phase = null;
        if (isObjective)
        {
            phase = ReadString(frontMatter, "phase", errors);
            if (phase is null && !IsPresent(frontMatter, "phase"))
                errors.Add(new("phase", "an objective requires phase (the slug of a phase document)."));
            else if (phase is not null && !IsSlug(phase))
                errors.Add(new("phase", $"phase must be a slug (lowercase letters, digits and hyphens), not '{phase}'."));
        }
        else if (isPhase && IsPresent(frontMatter, "phase"))
        {
            errors.Add(new("phase", "phase is only allowed on objectives."));
        }

        var owner = ReadString(frontMatter, "owner", errors);
        if (owner is not null && owner.Trim().Length == 0)
            errors.Add(new("owner", "owner must not be empty when present."));

        var dependsOn = ReadDependsOn(frontMatter, errors);
        if (isPhase && IsPresent(frontMatter, "depends_on"))
            errors.Add(new("depends_on", "depends_on is only allowed on objectives."));

        if (errors.Count > 0)
            return new RoadmapParseResult(null, [.. errors]);

        RoadmapDocument document = isObjective
            ? new RoadmapObjective(phase!, status!.Value, owner, dependsOn)
            : new RoadmapPhase(status!.Value);
        return new RoadmapParseResult(document, []);
    }

    /// <summary>
    /// Finds one dependency cycle with a simple depth-first search. <paramref name="dependsOnBySlug"/> maps each objective slug to
    /// the bare slugs it depends on. Dependencies that name no objective in the map add no edge. Returns the members of the
    /// first cycle found, in cycle order and starting from the cycle's first node in ordinal slug order; an empty list when the
    /// graph is acyclic. A self-dependency is a cycle of one.
    /// </summary>
    public static IReadOnlyList<string> FindDependencyCycle(IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOnBySlug)
    {
        ArgumentNullException.ThrowIfNull(dependsOnBySlug);
        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var start in dependsOnBySlug.Keys.Order(StringComparer.Ordinal))
        {
            if (state.ContainsKey(start))
                continue;
            var cycle = Visit(start, dependsOnBySlug, state, path);
            if (cycle.Count > 0)
                return cycle;
        }
        return [];
    }

    private enum VisitState
    {
        InProgress,
        Done,
    }

    private static List<string> Visit(
        string node,
        IReadOnlyDictionary<string, IReadOnlyList<string>> edges,
        Dictionary<string, VisitState> state,
        List<string> path)
    {
        state[node] = VisitState.InProgress;
        path.Add(node);

        if (edges.TryGetValue(node, out var targets))
        {
            foreach (var target in targets)
            {
                if (!edges.ContainsKey(target))
                    continue;

                if (state.TryGetValue(target, out var targetState))
                {
                    if (targetState == VisitState.InProgress)
                        return path.GetRange(path.IndexOf(target), path.Count - path.IndexOf(target));
                    continue;
                }

                var cycle = Visit(target, edges, state, path);
                if (cycle.Count > 0)
                    return cycle;
            }
        }

        path.RemoveAt(path.Count - 1);
        state[node] = VisitState.Done;
        return [];
    }

    /// <summary>A key counts as present when it has a non-null value; an explicit JSON null is the same as absent.</summary>
    private static bool IsPresent(JsonObject frontMatter, string key) =>
        frontMatter.TryGetPropertyValue(key, out var node) && node is not null;

    private static string? ReadString(JsonObject frontMatter, string key, List<RoadmapFrontMatterError> errors)
    {
        if (!frontMatter.TryGetPropertyValue(key, out var node) || node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        errors.Add(new(key, $"{key} must be a string."));
        return null;
    }

    private static RoadmapStatus? ReadStatus(JsonObject frontMatter, List<RoadmapFrontMatterError> errors)
    {
        var text = ReadString(frontMatter, "status", errors);
        if (text is null)
        {
            if (!IsPresent(frontMatter, "status"))
                errors.Add(new("status", "status is required (planned, active, done or dropped)."));
            return null;
        }

        return text switch
        {
            "planned" => RoadmapStatus.Planned,
            "active" => RoadmapStatus.Active,
            "done" => RoadmapStatus.Done,
            "dropped" => RoadmapStatus.Dropped,
            _ => Invalid(),
        };

        RoadmapStatus? Invalid()
        {
            errors.Add(new("status", $"status must be planned, active, done or dropped, not '{text}'."));
            return null;
        }
    }

    private static ImmutableArray<string> ReadDependsOn(JsonObject frontMatter, List<RoadmapFrontMatterError> errors)
    {
        if (!frontMatter.TryGetPropertyValue("depends_on", out var node) || node is null)
            return [];
        if (node is not JsonArray array)
        {
            errors.Add(new("depends_on", "depends_on must be a list of \"[[slug]]\" strings."));
            return [];
        }

        var slugs = ImmutableArray.CreateBuilder<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && WikiLinkPattern().Match(text) is { Success: true } match)
            {
                slugs.Add(match.Groups["slug"].Value);
            }
            else
            {
                errors.Add(new("depends_on", $"depends_on entry {item?.ToJsonString() ?? "null"} must be a wiki-link of the form [[slug]]."));
            }
        }
        return slugs.ToImmutable();
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^\[\[(?<slug>[a-z0-9-]+)\]\]$")]
    private static partial Regex WikiLinkPattern();
}
