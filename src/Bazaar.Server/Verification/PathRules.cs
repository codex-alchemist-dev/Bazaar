namespace Bazaar.Server.Verification;

/// <summary>Repo layout conventions the gate relies on (CLAUDE.md: projects under src/ and tests/).</summary>
public static class PathRules
{
    public static bool IsTestFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && Segments(path).Any(s =>
            s.Equals("tests", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase));

    public static bool IsUiFile(string path) =>
        path.StartsWith("src/Bazaar.Client", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase));

    /// <summary>An Avalonia.Headless test: a C# test file in a client test project.</summary>
    public static bool IsHeadlessTestFile(string path) =>
        IsTestFile(path) && Segments(path).Any(s => s.StartsWith("Bazaar.Client", StringComparison.OrdinalIgnoreCase) && s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase));

    public static bool IsSourceFile(string path) =>
        !IsTestFile(path) && path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

    /// <summary>Build entry points and CI config, where a masked failure or timeout would hide.</summary>
    public static bool IsBuildScript(string path) =>
        path is "build.sh" or "build.ps1" || path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase);

    private static string[] Segments(string path) => path.Split('/');
}
