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

    /// <summary>The generic renderer core namespace. Its source may use no other Bazaar namespace.</summary>
    public const string ConstellationCoreNamespace = "Bazaar.Constellation.Rendering.Core";

    /// <summary>Adapter-only identifiers that core source must not mention, even in comments or crefs.</summary>
    [GeneratedRegex(@"\b(IConstellationRenderer|ConstellationScene|VulkanSwapchain)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ConstellationAdapterIdentifier();

    /// <summary>The Rendering namespace or any name under it that is not under the core namespace.</summary>
    [GeneratedRegex(@"\bBazaar\.Constellation\.Rendering\b(?!\.Core\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ConstellationAdapterNamespace();

    /// <summary>A using directive, with an optional global, static or alias part, capturing the namespace.</summary>
    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?<ns>[\w.]+)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex UsingDirective();

    /// <summary>A namespace declaration at the start of a line, capturing the namespace.</summary>
    [GeneratedRegex(@"^\s*namespace\s+(?<ns>[\w.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceDeclaration();

    /// <summary>True for the core namespace and its child namespaces.</summary>
    private static bool IsCoreNamespace(string ns) =>
        ns == ConstellationCoreNamespace || ns.StartsWith(ConstellationCoreNamespace + ".", StringComparison.Ordinal);

    /// <summary>True when one core source line reaches outside the core namespace.</summary>
    private static bool IsConstellationCoreViolation(string line)
    {
        var usingMatch = UsingDirective().Match(line);
        if (usingMatch.Success)
        {
            var ns = usingMatch.Groups["ns"].Value;
            if (ns.StartsWith("Bazaar.", StringComparison.Ordinal) && !IsCoreNamespace(ns))
            {
                return true;
            }
        }

        var namespaceMatch = NamespaceDeclaration().Match(line);
        if (namespaceMatch.Success && !IsCoreNamespace(namespaceMatch.Groups["ns"].Value))
        {
            return true;
        }

        return ConstellationAdapterIdentifier().IsMatch(line) || ConstellationAdapterNamespace().IsMatch(line);
    }

    /// <summary>
    /// Core source lines that reach outside the core namespace: a using of another Bazaar namespace, a namespace
    /// declaration outside the core, or any mention of the adapter types or namespace. Findings are "path:line: text".
    /// </summary>
    public static IEnumerable<string> ConstellationCoreViolations(IEnumerable<(string Path, string Text)> coreSources)
    {
        foreach (var (path, text) in coreSources)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsConstellationCoreViolation(lines[i]))
                {
                    yield return $"{path}:{i + 1}: {lines[i].Trim()}";
                }
            }
        }
    }

    /// <summary>
    /// Referenced Bazaar assemblies that the core must not depend on. The Rendering assembly itself and names in the
    /// core namespace are allowed.
    /// </summary>
    public static IEnumerable<string> ConstellationCoreAssemblyViolations(IEnumerable<string> referencedNames) =>
        referencedNames.Where(n =>
            n.StartsWith("Bazaar.", StringComparison.Ordinal)
            && n != "Bazaar.Constellation.Rendering"
            && !IsCoreNamespace(n));
}
