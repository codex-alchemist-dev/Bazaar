using System.Text.Json;

namespace Bazaar.Architecture.Tests;

/// <summary>One allowlisted portability finding. Each entry names the audit finding it tracks (portability-audit.md).</summary>
/// <param name="Id">Audit finding id, for example <c>T1</c> or <c>K1</c>. <c>UNAUDITED-n</c> marks a finding the audit missed.</param>
/// <param name="Rule">Rule id, for example <c>P-01</c>.</param>
/// <param name="Path">Repo-relative forward-slash path or path prefix the finding must be under.</param>
/// <param name="Term">The matched term, or <c>*</c> for any term.</param>
/// <param name="Reason">Why the finding is tolerated until its audit finding is closed.</param>
internal sealed record PortabilityAllowEntry(string Id, string Rule, string Path, string Term, string Reason);

/// <summary>One forbidden vocabulary term. Matched as a whole word, with an optional trailing "s".</summary>
internal sealed record ForbiddenTerm(string Term, bool IgnoreCase);

/// <summary>A candidate library: the src project that is prepared for extraction.</summary>
internal sealed record PortabilityCandidate(string Project);

/// <summary>Configuration of the portability rules, loaded from <c>portability-candidates.json</c>.</summary>
internal sealed record PortabilityConfig(
    IReadOnlyList<PortabilityCandidate> Candidates,
    IReadOnlyList<ForbiddenTerm> ForbiddenTerms,
    IReadOnlyList<string> NamespaceTerms,
    IReadOnlyList<string> KeyPrefixes,
    IReadOnlyList<string> HostReferences,
    IReadOnlyList<string> ProviderTerms,
    IReadOnlyList<PortabilityAllowEntry> Allowlist)
{
    private const string _file = "tests/Bazaar.Architecture.Tests/Portability/portability-candidates.json";

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PortabilityConfig Load() =>
        Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, _file)));

    public static PortabilityConfig Parse(string json) =>
        JsonSerializer.Deserialize<PortabilityConfig>(json, _options)
        ?? throw new InvalidOperationException("portability-candidates.json is empty.");

    /// <summary>Candidate projects whose folder exists under src. Missing ones (for example Cdax.Core) are skipped, not failures.</summary>
    public IEnumerable<string> PresentProjects() =>
        Candidates.Select(c => c.Project).Where(p => Directory.Exists(RepoPaths.SourceDirectory(p)));

    /// <summary>Candidate projects whose folder does not exist yet.</summary>
    public IEnumerable<string> MissingProjects() =>
        Candidates.Select(c => c.Project).Where(p => !Directory.Exists(RepoPaths.SourceDirectory(p)));
}

/// <summary>A portability finding: rule, repo-relative path, 1-based line (0 for a file-level finding), matched term and text.</summary>
internal sealed record PortabilityFinding(string Rule, string Path, int Line, string Term, string Text)
{
    public override string ToString() => $"{Rule} {Path}:{Line}: [{Term}] {Text}";
}
