using System.Text.RegularExpressions;

namespace Bazaar.Architecture.Tests;

/// <summary>
/// Portability rules P-01, P-02, P-03, P-07 and P-08 of docs/design/85-extraction-portability.md over the extraction
/// candidates in <c>portability-candidates.json</c>. Existing findings are allowlisted, each naming its audit finding
/// (portability-audit.md). A new finding fails the build; an allowlist entry that no longer matches anything fails
/// until it is removed, so the list only shrinks. Candidates that do not exist yet (Cdax.Core) are skipped.
/// </summary>
public partial class PortabilityTests
{
    private static readonly PortabilityConfig _config = PortabilityConfig.Load();

    [GeneratedRegex(@"^(?:[A-Z]{1,2}\d+|UNAUDITED-\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowId();

    private static List<PortabilityFinding> Findings(string rule)
    {
        var projects = _config.PresentProjects().ToList();
        var sources = ArchitectureRules.PortabilityFiles(projects, "*.cs").ToList();
        var projectFiles = ArchitectureRules.PortabilityFiles(projects, "*.csproj").ToList();
        return (rule switch
        {
            "P-01" => ArchitectureRules.VocabularyFindings(sources, _config),
            "P-02" => ArchitectureRules.HostPrefixFindings(sources, _config),
            "P-03" => ArchitectureRules.PackageReferenceFindings(projectFiles, _config),
            "P-07" => ArchitectureRules.HostAssumptionFindings(projectFiles, sources, _config),
            "P-08" => ArchitectureRules.ProviderFindings(projectFiles, sources, _config),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        }).ToList();
    }

    private static string Dump(IEnumerable<PortabilityFinding> findings) =>
        string.Join('\n', findings.Select(f => f.ToString()));

    [Theory]
    [InlineData("P-01")]
    [InlineData("P-02")]
    [InlineData("P-03")]
    [InlineData("P-07")]
    [InlineData("P-08")]
    public void No_new_portability_findings(string rule)
    {
        var added = ArchitectureRules.Unallowed(Findings(rule), _config).ToList();

        Assert.True(
            added.Count == 0,
            $"New {rule} findings in an extraction candidate (docs/design/85 section 3a). Fix them, or allowlist with an audit "
            + "finding id in tests/Bazaar.Architecture.Tests/Portability/portability-candidates.json:\n" + Dump(added));
    }

    [Theory]
    [InlineData("P-01")]
    [InlineData("P-02")]
    [InlineData("P-03")]
    [InlineData("P-07")]
    [InlineData("P-08")]
    public void Allowlist_has_no_stale_entries(string rule)
    {
        var findings = Findings(rule);
        var stale = ArchitectureRules.StaleEntries(findings, _config with { Allowlist = [.. _config.Allowlist.Where(e => e.Rule == rule)] })
            .Select(e => $"{e.Id} {e.Rule} {e.Path} [{e.Term}]")
            .ToList();

        Assert.True(
            stale.Count == 0,
            "Remove these entries from portability-candidates.json (finding fixed or file moved):\n" + string.Join('\n', stale));
    }

    [Fact]
    public void Allowlist_entries_name_an_audit_finding_and_a_known_rule()
    {
        string[] rules = ["P-01", "P-02", "P-03", "P-07", "P-08"];

        Assert.All(_config.Allowlist, e =>
        {
            Assert.Matches(AllowId(), e.Id);
            Assert.Contains(e.Rule, rules);
            Assert.False(string.IsNullOrWhiteSpace(e.Reason), $"{e.Id} needs a reason.");
            Assert.StartsWith("src/", e.Path, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Candidate_list_scans_at_least_one_project_and_skips_missing_ones()
    {
        var present = _config.PresentProjects().ToList();

        // A config that matched no folder would pass every rule vacuously.
        Assert.NotEmpty(present);
        Assert.NotEmpty(ArchitectureRules.PortabilityFiles(present, "*.cs"));
        Assert.Empty(present.Intersect(_config.MissingProjects()));
    }

    [Fact]
    public void Skipped_rules_are_listed()
    {
        string[] skipped = ["P-04", "P-05", "P-06", "R-47", "R-48", "R-49"];

        Assert.Equal(skipped.Order(), ArchitectureRules.SkippedRules.Keys.Order());
    }
}
