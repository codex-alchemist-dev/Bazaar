using System.Text;

namespace Bazaar.Rubedo.Docs.Tool;

/// <summary>
/// The command line of the reference tool (doc 41d section 6): <c>--out &lt;dir&gt;</c> writes the pages, <c>--check &lt;dir&gt;</c>
/// exits 1 when the folder differs from regenerated output. Exit codes: 0 ok, 1 drift, 2 bad usage.
/// </summary>
public static class DocsToolRunner
{
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);

        // 'dotnet run -- --out x' may hand the separator through; accept it.
        if (args.Length > 0 && args[0] == "--")
        {
            args = args[1..];
        }

        if (args.Length != 2 || (args[0] != "--out" && args[0] != "--check"))
        {
            error.WriteLine("Usage: bazaar-rubedo-docs --out <dir> | --check <dir>");
            return 2;
        }

        string folder = args[1];
        IReadOnlyList<RubedoReferencePage> pages = RubedoReferenceGenerator.Generate();
        return args[0] == "--out" ? Write(folder, pages, output) : Check(folder, pages, output);
    }

    private static int Write(string folder, IReadOnlyList<RubedoReferencePage> pages, TextWriter output)
    {
        Directory.CreateDirectory(folder);
        foreach (RubedoReferencePage page in pages)
        {
            string path = Path.Combine(folder, page.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, page.Text, _utf8);
        }

        // Drop generated pages that no longer exist, so a removed page cannot linger.
        foreach (string stale in FindMarkdown(folder).Where(f => pages.All(p => p.RelativePath != f)))
        {
            File.Delete(Path.Combine(folder, stale));
        }

        output.WriteLine($"Wrote {pages.Count} pages to {folder}.");
        return 0;
    }

    private static int Check(string folder, IReadOnlyList<RubedoReferencePage> pages, TextWriter output)
    {
        var problems = new List<string>();
        foreach (RubedoReferencePage page in pages)
        {
            string path = Path.Combine(folder, page.RelativePath);
            if (!File.Exists(path))
            {
                problems.Add($"missing: {page.RelativePath}");
            }
            else if (File.ReadAllText(path, _utf8) != page.Text)
            {
                problems.Add($"differs: {page.RelativePath}");
            }
        }

        foreach (string extra in FindMarkdown(folder).Where(f => pages.All(p => p.RelativePath != f)))
        {
            problems.Add($"unexpected: {extra}");
        }

        if (problems.Count == 0)
        {
            output.WriteLine("The reference is up to date.");
            return 0;
        }

        foreach (string problem in problems)
        {
            output.WriteLine(problem);
        }

        output.WriteLine("The committed reference differs from the language. Regenerate it with --out.");
        return 1;
    }

    private static IEnumerable<string> FindMarkdown(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
            : [];
}
