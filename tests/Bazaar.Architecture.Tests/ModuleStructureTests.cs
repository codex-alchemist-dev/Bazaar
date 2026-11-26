namespace Bazaar.Architecture.Tests;

/// <summary>
/// Prevention for the drift found in docs/audit/DRIFT-2026-11-16.md. Decided structure in docs/design/ is binding:
/// every feature is its own module project on Bazaar.Core, and Bazaar.Server and Bazaar.Client.Core hold no new feature code.
/// Existing violations are listed in <c>architecture-baseline.txt</c>. A new file in those projects fails the build until it is
/// listed, and a listed file that has moved or been deleted fails until the baseline is trimmed, so the list only shrinks.
/// </summary>
public class ModuleStructureTests
{
    private const string _baselineFile = "tests/Bazaar.Architecture.Tests/architecture-baseline.txt";
    private static readonly string[] _ratchetedProjects = ["src/Bazaar.Server", "src/Bazaar.Client.Core"];

    /// <summary>Lists every .cs file under the ratcheted projects, as repo-relative forward-slash paths.</summary>
    private static HashSet<string> CurrentRatchetedFiles() =>
        _ratchetedProjects
            .Select(p => Path.Combine(RepoPaths.Root, p))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Select(f => Path.GetRelativePath(RepoPaths.Root, f).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Baseline() =>
        File.ReadAllLines(Path.Combine(RepoPaths.Root, _baselineFile))
            .Select(l => l.Trim().Replace('\\', '/'))
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>New feature code must go in its own module project. A new file here means a feature was added to the wrong place.</summary>
    [Fact]
    public void No_new_files_in_server_or_client_core()
    {
        var baseline = Baseline();
        var added = CurrentRatchetedFiles().Where(f => !baseline.Contains(f)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            added.Length == 0,
            "New .cs files in Bazaar.Server or Bazaar.Client.Core. Put features in their own module project on Bazaar.Core "
            + "(docs/design/40, 41b; DECISIONS-LOG). Files found:\n" + string.Join('\n', added));
    }

    /// <summary>The baseline must only shrink. A listed file that no longer exists was moved or extracted, so remove its line.</summary>
    [Fact]
    public void Baseline_has_no_stale_entries()
    {
        var current = CurrentRatchetedFiles();
        var stale = Baseline().Where(f => !current.Contains(f)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            stale.Length == 0,
            "Remove these lines from tests/Bazaar.Architecture.Tests/architecture-baseline.txt (moved or deleted):\n"
            + string.Join('\n', stale));
    }

    /// <summary>
    /// Target state from docs/design/40 and CLAUDE.md: a Bazaar.Core project exists, and the server host and client heads load modules through it.
    /// Skipped until Bazaar.Client.Core references Bazaar.Core (the server kernel is in Bazaar.Core since #625).
    /// </summary>
    [Fact]
    public void Bazaar_core_project_exists_and_is_referenced_by_hosts()
    {
        var corePath = RepoPaths.ProjectFile("Bazaar.Core");
        Assert.True(File.Exists(corePath), "Bazaar.Core project is missing.");

        foreach (var host in new[] { "Bazaar.Server.Host", "Bazaar.Client.Core" })
        {
            var refs = ArchitectureRules.ProjectReferenceNames(RepoPaths.ProjectFile(host)).ToArray();
            Assert.Contains(refs, r => r is "Bazaar.Core" or "Bazaar.Core.Hosting");
        }
    }

    /// <summary>
    /// Bazaar.Core must stay dependency-free so it can become a standalone repo (docs/design/BAZAAR-COMPONENTS.md section 13):
    /// no package references and no project references. The dependency-injection and configuration entry point lives in Bazaar.Core.Hosting.
    /// </summary>
    [Fact]
    public void Bazaar_core_has_no_package_or_project_references()
    {
        var project = System.Xml.Linq.XDocument.Load(RepoPaths.ProjectFile("Bazaar.Core"));

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    /// <summary>Every PR body and the PR template must carry the docs-section checklist item (CLAUDE.md, PR checklist rule).</summary>
    [Fact]
    public void Pull_request_template_names_the_docs_section_implemented()
    {
        var template = File.ReadAllText(Path.Combine(RepoPaths.Root, ".github", "pull_request_template.md"));

        Assert.Contains("Docs section this implements:", template);
    }

    /// <summary>The working rules must state the structure rule at the top, so every agent reads it before coding.</summary>
    [Fact]
    public void Claude_rules_state_the_structure_rule()
    {
        var rules = File.ReadAllText(Path.Combine(RepoPaths.Root, "CLAUDE.md"));

        Assert.Contains("## Design docs are law", rules);
        Assert.Contains("its own module project on Bazaar.Core", rules);
    }
}
