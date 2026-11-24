using System.Text.RegularExpressions;

namespace Bazaar.Client.Theme.Tests;

/// <summary>
/// Doc 93, Everything is a Rubedo-overridable token: views reference tokens by name only. This scans view source for literal
/// colours and durations. It covers the shared view layer today; a project joins <see cref="_scannedProjects"/> once it is clean.
/// </summary>
public partial class ViewLiteralScanTests
{
    /// <summary>Source projects held to the no-literals rule.</summary>
    private static readonly string[] _scannedProjects = ["src/Bazaar.Client.Views", "src/Bazaar.Client.DesktopChrome"];

    /// <summary>Files that are the token-resolution point or the built-in theme definitions, so they hold colour text by design.</summary>
    private static readonly string[] _allowed = ["ViewTheme.cs", "ChromeTheme.cs", "BuiltInThemes.cs"];

    [GeneratedRegex(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b|Color\.(?:FromRgb|FromArgb)\(|Brushes\.|Colors\.|TimeSpan\.From|Duration\s*=\s*[0-9]")]
    private static partial Regex Literal();

    [Fact]
    public void View_source_has_no_literal_colours_or_durations()
    {
        string root = FindRepoRoot();
        var found = new List<string>();
        foreach (string project in _scannedProjects)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, project), "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                    && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !_allowed.Contains(Path.GetFileName(f), StringComparer.Ordinal)))
            {
                found.AddRange(Scan(File.ReadAllText(file)).Select(hit => $"{Path.GetRelativePath(root, file)}: {hit}"));
            }
        }

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("var c = \"#FF8800\";", true)]
    [InlineData("var c = \"#FF880080\";", true)]
    [InlineData("var b = Brushes.Red;", true)]
    [InlineData("var d = TimeSpan.FromMilliseconds(200);", true)]
    [InlineData("var c = Color.FromRgb(1, 2, 3);", true)]
    [InlineData("var b = theme.Brush(\"color.accent.default\");", false)]
    [InlineData("var d = theme.Duration(\"motion.duration.sheet\");", false)]
    public void Scanner_flags_literals_and_not_token_lookups(string line, bool flagged)
    {
        Assert.Equal(flagged, Scan(line).Any());
    }

    [Fact]
    public void Comments_are_not_flagged()
    {
        Assert.Empty(Scan("/// the theme writes #RRGGBBAA, e.g. #FF8800AA\n// TimeSpan.FromSeconds is banned here\n"));
    }

    private static IEnumerable<string> Scan(string source)
    {
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = Literal().Match(line);
            if (match.Success)
            {
                yield return match.Value;
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bazaar.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
