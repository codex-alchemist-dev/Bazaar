namespace Bazaar.Architecture.Tests;

/// <summary>
/// Feature modules must not reference the server library directly. Each module is its own project on Bazaar.Core
/// (docs/design/40, CLAUDE.md "Design docs are law"), and Bazaar.Server is the server kernel that hosts load, not a module dependency.
/// Existing offenders are listed by project name in <c>module-server-reference-baseline.txt</c>. A new offender fails until it drops
/// the reference, and a listed project that no longer offends fails until its line is removed, so the list only shrinks.
/// </summary>
public class ModuleServerReferenceTests
{
    private const string _baselineFile = "tests/Bazaar.Architecture.Tests/module-server-reference-baseline.txt";

    /// <summary>Every src project declared in the repo, with its ProjectReference names read from disk.</summary>
    private static IEnumerable<(string Project, IEnumerable<string> References)> SrcProjects() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Select(f => (Project: Path.GetFileNameWithoutExtension(f), References: ArchitectureRules.ProjectReferenceNames(f)));

    private static HashSet<string> CurrentOffenders() =>
        ArchitectureRules.ModuleServerReferences(SrcProjects()).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Baseline() =>
        File.ReadAllLines(Path.Combine(RepoPaths.Root, _baselineFile))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>A feature module that references Bazaar.Server is a new violation unless it is already in the baseline.</summary>
    [Fact]
    public void No_new_module_project_references_bazaar_server()
    {
        var baseline = Baseline();
        var added = CurrentOffenders().Where(p => !baseline.Contains(p)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            added.Length == 0,
            "Feature module projects must not reference src/Bazaar.Server (depend on Bazaar.Core and the module's Abstractions instead). "
            + "Projects found:\n" + string.Join('\n', added));
    }

    /// <summary>The baseline must only shrink. A listed project that no longer references Bazaar.Server, or no longer exists, needs its line removed.</summary>
    [Fact]
    public void Module_server_reference_baseline_has_no_stale_entries()
    {
        var current = CurrentOffenders();
        var stale = Baseline().Where(p => !current.Contains(p)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            stale.Length == 0,
            "Remove these lines from tests/Bazaar.Architecture.Tests/module-server-reference-baseline.txt (reference removed or project gone):\n"
            + string.Join('\n', stale));
    }

    /// <summary>Every exempt name must be a real src project, so a renamed or deleted project cannot leave a dead exemption behind.</summary>
    [Fact]
    public void Server_reference_exemptions_name_existing_projects()
    {
        var projects = SrcProjects().Select(p => p.Project).ToHashSet(StringComparer.Ordinal);
        var missing = ArchitectureRules.ServerReferenceExemptProjects.Where(p => !projects.Contains(p)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0, "Server-reference exemptions with no src project: " + string.Join(", ", missing));
    }
}
