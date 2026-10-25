using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bazaar.Architecture.Tests;

/// <summary>
/// Pure rule checks. Each takes plain names or source text and returns findings, so the seeded-violation
/// tests can prove the rules fire without touching the real tree.
/// </summary>
internal static partial class ArchitectureRules
{
    /// <summary>Assembly-name prefixes of libraries that only exist on Windows.</summary>
    public static readonly string[] WindowsOnlyAssemblyPrefixes =
    [
        "Microsoft.Win32",
        "System.Management",
        "System.ServiceProcess",
        "System.Diagnostics.EventLog",
        "System.DirectoryServices",
        "System.Security.Principal.Windows",
        "Microsoft.Extensions.Hosting.WindowsServices",
    ];

    /// <summary>Source patterns that mark Windows-only code or a Windows platform attribute.</summary>
    [GeneratedRegex(@"\b(Microsoft\.Win32|System\.Management|System\.ServiceProcess|System\.Diagnostics\.EventLog|System\.DirectoryServices|WindowsIdentity|WindowsServices)\b|SupportedOSPlatform\(\s*""windows", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsOnlySource();

    /// <summary>Names that start with <paramref name="prefix"/> and are not in <paramref name="allowed"/>.</summary>
    private static IEnumerable<string> Matching(IEnumerable<string> names, string prefix, params string[] allowed) =>
        names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && !allowed.Contains(n, StringComparer.Ordinal));

    /// <summary>Referenced assembly names that are Windows-only.</summary>
    public static IEnumerable<string> WindowsOnlyReferences(IEnumerable<string> referencedNames) =>
        referencedNames.Where(n => WindowsOnlyAssemblyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>Source text lines that contain Windows-only API use, as "path:line: text".</summary>
    public static IEnumerable<string> WindowsOnlySourceFindings(IEnumerable<(string Path, string Text)> sources)
    {
        foreach (var (path, text) in sources)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (WindowsOnlySource().IsMatch(lines[i]))
                {
                    yield return $"{path}:{i + 1}: {lines[i].Trim()}";
                }
            }
        }
    }

    /// <summary>Names of Bazaar client heads (Windows, Linux, Android) that Client.Core must not reference.</summary>
    public static IEnumerable<string> ClientHeadReferences(IEnumerable<string> referencedNames) =>
        Matching(referencedNames, "Bazaar.Client.", "Bazaar.Client.Core");

    /// <summary>Names of Bazaar server assemblies that Contracts must not reference.</summary>
    public static IEnumerable<string> ServerReferences(IEnumerable<string> referencedNames) =>
        Matching(referencedNames, "Bazaar.Server");

    /// <summary>ProjectReference names declared in a .csproj file.</summary>
    public static IEnumerable<string> ProjectReferenceNames(string csprojPath) =>
        XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
            .Where(n => n.Length > 0);

    /// <summary>Source files under the given src project folders, skipping build output.</summary>
    public static IEnumerable<(string Path, string Text)> SourceFiles(IEnumerable<string> projectDirectories) =>
        projectDirectories
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(RepoPaths.Root, f), File.ReadAllText(f)));

    /// <summary>Assembly names referenced by a compiled assembly.</summary>
    public static IEnumerable<string> ReferencedNames(System.Reflection.Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);
}
