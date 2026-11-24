using Bazaar.Server.Knowledge.Metrics;

namespace Bazaar.Server.Health;

/// <summary>A reverted merge, as the forge adapter reports it (doc 21).</summary>
public sealed record RevertFact(string Sha, DateTimeOffset At);

/// <summary>A card moved back out of Done (doc 12), from a Kanban or <c>gc_event</c> record.</summary>
public sealed record ReopenFact(string CardId, DateTimeOffset At);

/// <summary>A Council vote that was decided the other way on a re-vote (doc 10), from a <c>gc_event</c> record.</summary>
public sealed record VoteReversalFact(string VoteId, DateTimeOffset At);

/// <summary>A Cobertura coverage report for one toolchain.</summary>
public sealed record CoverageReportFact(string Toolchain, string CoberturaXml, string SourceRef);

/// <summary>A SARIF analyzer log for one toolchain.</summary>
public sealed record AnalyzerReportFact(string Toolchain, string Sarif, string SourceRef);

/// <summary>The output of <c>dotnet list package --vulnerable --format json</c> (doc 21 package audit).</summary>
public sealed record PackageAuditFact(string Json, string SourceRef);

/// <summary>How many cards the board holds and how many are Blocked on the day (doc 12).</summary>
public sealed record BoardCountsFact(int BlockedCards, int TotalCards);

/// <summary>
/// Everything one project reported for one UTC day. Every list may be empty and every single value may be null: a source that
/// has nothing to say contributes nothing, and the collector records a gap, never a zero.
/// </summary>
public sealed record HealthFacts
{
    public static HealthFacts Empty { get; } = new();

    public IReadOnlyList<CiRunFact> CiRuns { get; init; } = [];
    public IReadOnlyList<CoverageReportFact> Coverage { get; init; } = [];
    public IReadOnlyList<AnalyzerReportFact> Analyzer { get; init; } = [];
    public PackageAuditFact? PackageAudit { get; init; }
    public IReadOnlyList<RevertFact> Reverts { get; init; } = [];
    public IReadOnlyList<ReopenFact> Reopens { get; init; } = [];
    public IReadOnlyList<VoteReversalFact> VoteReversals { get; init; } = [];
    public BoardCountsFact? Board { get; init; }

    /// <summary>Joins what several sources reported. Lists concatenate; for a single value the first source that has one wins.</summary>
    public static HealthFacts Merge(IEnumerable<HealthFacts> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var all = parts.ToList();
        return new HealthFacts
        {
            CiRuns = [.. all.SelectMany(p => p.CiRuns)],
            Coverage = [.. all.SelectMany(p => p.Coverage)],
            Analyzer = [.. all.SelectMany(p => p.Analyzer)],
            PackageAudit = all.Select(p => p.PackageAudit).FirstOrDefault(a => a is not null),
            Reverts = [.. all.SelectMany(p => p.Reverts)],
            Reopens = [.. all.SelectMany(p => p.Reopens)],
            VoteReversals = [.. all.SelectMany(p => p.VoteReversals)],
            Board = all.Select(p => p.Board).FirstOrDefault(b => b is not null),
        };
    }
}

/// <summary>Lists the projects whose health is collected. A host module that knows the projects registers one.</summary>
public interface IHealthProjectSource
{
    Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A module's contribution of health inputs (CI from the forge adapter, analyzer and audit output, Kanban, <c>gc_event</c>). Several
/// may be registered; the collector merges them. Read-only: a source must not write anywhere.
/// </summary>
public interface IHealthFactSource
{
    /// <summary>What this source knows about <paramref name="projectId"/> for the UTC <paramref name="day"/>.</summary>
    Task<HealthFacts> ReadAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default);
}
