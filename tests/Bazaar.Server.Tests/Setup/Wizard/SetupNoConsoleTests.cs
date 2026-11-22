using System.Text.RegularExpressions;

namespace Bazaar.Server.Tests.Setup.Wizard;

/// <summary>
/// The setup modules are UI-free (doc 45 section 1.1): a console and a terminal UI both drive them through ISetupPrompter, so no
/// source file under Bazaar.Server.Setup* may use System.Console.
/// </summary>
public sealed partial class SetupNoConsoleTests
{
    [Fact]
    public void No_setup_module_source_references_System_Console()
    {
        var root = FindRoot(AppContext.BaseDirectory);
        var projects = Directory.GetDirectories(Path.Combine(root, "src"), "Bazaar.Server.Setup*");
        Assert.NotEmpty(projects);

        var offenders = projects
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(file => ConsoleUse().IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\bConsole\s*\.|System\.Console|using\s+static\s+System\.Console")]
    private static partial Regex ConsoleUse();

    private static string FindRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bazaar.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Bazaar.slnx not found.");
    }
}
