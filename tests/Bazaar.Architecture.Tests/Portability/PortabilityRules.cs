using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bazaar.Architecture.Tests;

/// <summary>
/// Portability rules of docs/design/85-extraction-portability.md section 3a for the extraction candidates. Each rule
/// is a pure function over (Path, Text) sources and the config, so the seeded tests can prove it fires. Paths are
/// repo-relative with forward slashes. P-04 to P-06 and R-47 to R-49 are not checked here; see <see cref="SkippedRules"/>.
/// </summary>
internal static partial class ArchitectureRules
{
    /// <summary>Rules of doc 85 that this engine does not check yet, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> SkippedRules = new Dictionary<string, string>
    {
        ["P-04"] = "needs the sample consumer (P-06), which does not exist yet",
        ["P-05"] = "CI public-API check and version template, not a source scan",
        ["P-06"] = "sample consumer template, a later work package",
        ["R-47"] = "settings keys: the P-02 scan covers literals; the registry does not exist as a library yet",
        ["R-48"] = "DI and logging adapter modules, a later work package",
        ["R-49"] = "sample consumer template, a later work package",
    };

    [GeneratedRegex(@"^\s*namespace\s+(?<ns>[\w.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PortabilityNamespaceDeclaration();

    [GeneratedRegex(@"\b(?:class|struct|interface|enum|record)\s+(?<name>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex PortabilityTypeDeclaration();

    [GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*\"", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();

    /// <summary>Repo-relative forward-slash path.</summary>
    public static string PortabilityPath(string fullPath) =>
        Path.GetRelativePath(RepoPaths.Root, fullPath).Replace('\\', '/');

    /// <summary>The files matching <paramref name="pattern"/> in the given candidate projects, as (repo-relative path, text).</summary>
    public static IEnumerable<(string Path, string Text)> PortabilityFiles(IEnumerable<string> projects, string pattern) =>
        projects
            .Select(RepoPaths.SourceDirectory)
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, pattern, SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(f => (PortabilityPath(f), File.ReadAllText(f)));

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static Regex TermRegex(ForbiddenTerm t) =>
        new(@"\b" + Regex.Escape(t.Term) + @"s?\b", RegexOptions.CultureInvariant | (t.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));

    /// <summary>
    /// P-01: forbidden vocabulary in source and XML docs, and the product name in namespaces and public type names.
    /// A forbidden term is a whole word, with an optional trailing "s".
    /// </summary>
    public static IEnumerable<PortabilityFinding> VocabularyFindings(IEnumerable<(string Path, string Text)> sources, PortabilityConfig config)
    {
        var terms = config.ForbiddenTerms.Select(t => (t.Term, Regex: TermRegex(t))).ToList();
        foreach (var (path, text) in sources)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (term, regex) in terms)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        yield return new PortabilityFinding("P-01", path, i + 1, term, lines[i].Trim());
                    }
                }

                foreach (var term in config.NamespaceTerms)
                {
                    if (NamesTerm(lines[i], term))
                    {
                        yield return new PortabilityFinding("P-01", path, i + 1, term, lines[i].Trim());
                    }
                }
            }
        }
    }

    /// <summary>True when the line declares a namespace, or a public or protected type, whose name contains the term.</summary>
    private static bool NamesTerm(string line, string term)
    {
        var ns = PortabilityNamespaceDeclaration().Match(line);
        if (ns.Success)
        {
            return ns.Groups["ns"].Value.Contains(term, StringComparison.Ordinal);
        }

        if (IsComment(line) || !(line.Contains("public ", StringComparison.Ordinal) || line.Contains("protected ", StringComparison.Ordinal)))
        {
            return false;
        }

        var type = PortabilityTypeDeclaration().Match(line);
        return type.Success && type.Groups["name"].Value.Contains(term, StringComparison.Ordinal);
    }

    /// <summary>
    /// P-02: string literals, environment variables and source file names that start with a host prefix (for example
    /// <c>"bazaar.audit.v1"</c>, <c>BAZAAR_TEST_SEED</c>, <c>bazaar_t_</c>, <c>Bazaar:Components:</c>).
    /// </summary>
    public static IEnumerable<PortabilityFinding> HostPrefixFindings(IEnumerable<(string Path, string Text)> sources, PortabilityConfig config)
    {
        foreach (var prefix in config.KeyPrefixes)
        {
            var literal = new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(prefix) + @"[._:\-]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            foreach (var (path, text) in sources)
            {
                if (Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new PortabilityFinding("P-02", path, 0, prefix, "file name starts with the host prefix");
                }

                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!IsComment(lines[i]) && StringLiteral().Matches(lines[i]).Any(m => literal.IsMatch(m.Value)))
                    {
                        yield return new PortabilityFinding("P-02", path, i + 1, prefix, lines[i].Trim());
                    }
                }
            }
        }
    }

    /// <summary>The PackageReference and ProjectReference names of a project file, each as (kind, name).</summary>
    public static IEnumerable<(string Kind, string Name)> PortabilityReferences(string csprojText)
    {
        var doc = XDocument.Parse(csprojText);
        foreach (var e in doc.Descendants("PackageReference"))
        {
            yield return ("package", e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? string.Empty);
        }

        foreach (var e in doc.Descendants("ProjectReference"))
        {
            yield return ("project", Path.GetFileNameWithoutExtension((e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')));
        }
    }

    private static bool IsHostReference(string name, PortabilityConfig config) =>
        config.HostReferences.Any(h => name == h || name.StartsWith(h + ".", StringComparison.Ordinal));

    private static bool IsProviderName(string name, PortabilityConfig config) =>
        config.ProviderTerms.Any(p => name.Split('.', '/', '-').Contains(p, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// P-03: any package reference, and any project reference that is neither another candidate nor a host reference
    /// (host references are P-07). A shared library carries the .NET base library and sibling candidates only.
    /// </summary>
    public static IEnumerable<PortabilityFinding> PackageReferenceFindings(IEnumerable<(string Path, string Text)> projectFiles, PortabilityConfig config)
    {
        var candidates = config.Candidates.Select(c => c.Project).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, text) in projectFiles)
        {
            foreach (var (kind, name) in PortabilityReferences(text))
            {
                if (kind == "package" || (!candidates.Contains(name) && !IsHostReference(name, config)))
                {
                    yield return new PortabilityFinding("P-03", path, 0, name, $"{kind} reference");
                }
            }
        }
    }

    /// <summary>
    /// P-07: host assumptions. A reference to a host project or package (Bazaar.Server, Bazaar.Contracts, Npgsql) in a
    /// project file, and a mention of one in source (a using, a qualified name or a type such as NpgsqlConnection).
    /// </summary>
    public static IEnumerable<PortabilityFinding> HostAssumptionFindings(
        IEnumerable<(string Path, string Text)> projectFiles,
        IEnumerable<(string Path, string Text)> sources,
        PortabilityConfig config)
    {
        foreach (var (path, text) in projectFiles)
        {
            foreach (var (kind, name) in PortabilityReferences(text).Where(r => IsHostReference(r.Name, config) || config.HostReferences.Contains(r.Name)))
            {
                yield return new PortabilityFinding("P-07", path, 0, name, $"{kind} reference");
            }
        }

        foreach (var (path, text) in sources)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var host in config.HostReferences.Where(h => HostMention(lines[i], h)))
                {
                    yield return new PortabilityFinding("P-07", path, i + 1, host, lines[i].Trim());
                }
            }
        }
    }

    /// <summary>True when the host name appears in the line, not as the tail of a longer word.</summary>
    private static bool HostMention(string line, string host)
    {
        var start = 0;
        while ((start = line.IndexOf(host, start, StringComparison.Ordinal)) >= 0)
        {
            var before = start == 0 ? ' ' : line[start - 1];
            if (!char.IsLetterOrDigit(before) && before != '_')
            {
                return true;
            }

            start += host.Length;
        }

        return false;
    }

    /// <summary>
    /// P-08: provider code and provider dependencies in a shared library: a package or project reference whose name
    /// carries a provider term, and source files under a folder named for a provider (for example Postgres, Vulkan).
    /// </summary>
    public static IEnumerable<PortabilityFinding> ProviderFindings(
        IEnumerable<(string Path, string Text)> projectFiles,
        IEnumerable<(string Path, string Text)> sources,
        PortabilityConfig config)
    {
        foreach (var (path, text) in projectFiles)
        {
            foreach (var (kind, name) in PortabilityReferences(text).Where(r => IsProviderName(r.Name, config)))
            {
                yield return new PortabilityFinding("P-08", path, 0, name, $"{kind} reference");
            }
        }

        foreach (var (path, _) in sources)
        {
            // Segments are src/<project>/<folders...>/<file>; only the folders below the project count.
            foreach (var folder in path.Split('/').Skip(2).SkipLast(1))
            {
                if (config.ProviderTerms.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    yield return new PortabilityFinding("P-08", path, 0, folder, "source under a provider folder");
                }
            }
        }
    }

    /// <summary>True when the allowlist entry covers the finding: same rule, path at or under the entry path, and term.</summary>
    public static bool IsAllowed(PortabilityAllowEntry entry, PortabilityFinding finding) =>
        entry.Rule == finding.Rule
        && (finding.Path == entry.Path || finding.Path.StartsWith(entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/", StringComparison.Ordinal))
        && (entry.Term == "*" || string.Equals(entry.Term, finding.Term, StringComparison.OrdinalIgnoreCase));

    /// <summary>Findings no allowlist entry covers.</summary>
    public static IEnumerable<PortabilityFinding> Unallowed(IEnumerable<PortabilityFinding> findings, PortabilityConfig config) =>
        findings.Where(f => !config.Allowlist.Any(e => IsAllowed(e, f)));

    /// <summary>Allowlist entries that cover no finding. Fixed findings must lose their entry, so the list only shrinks.</summary>
    public static IEnumerable<PortabilityAllowEntry> StaleEntries(IReadOnlyCollection<PortabilityFinding> findings, PortabilityConfig config) =>
        config.Allowlist.Where(e => !findings.Any(f => IsAllowed(e, f)));
}
