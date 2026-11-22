using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.Docs.Tests;

/// <summary>Tests of the generated reference (doc 41d section 6): determinism, completeness, worked examples and the CI drift check.</summary>
public partial class ReferenceTests
{
    private static readonly string[] _scannedProjects = ["Bazaar.Rubedo", "Bazaar.Rubedo.Analysis"];

    private static readonly string[] _actionWireNames = ["navigate", "invoke", "openPane", "setSetting"];

    private static string Corpus => File.ReadAllText(RepoPaths.GoldenCorpus);

    private static string Page(string path) => RubedoReferenceGenerator.Generate().Single(p => p.RelativePath == path).Text;

    [Fact]
    public void Two_runs_are_identical()
    {
        var first = RubedoReferenceGenerator.Generate();
        var second = RubedoReferenceGenerator.Generate();

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_embedded_corpus_is_the_golden_corpus()
    {
        Assert.Equal(RubedoReferenceGenerator.Generate(), RubedoReferenceGenerator.Generate(Corpus));
    }

    [Fact]
    public void Pages_are_ordered_use_lf_and_end_with_one_newline()
    {
        var pages = RubedoReferenceGenerator.Generate();

        Assert.Equal(pages.Select(p => p.RelativePath).OrderBy(p => p, StringComparer.Ordinal), pages.Select(p => p.RelativePath));
        foreach (RubedoReferencePage page in pages)
        {
            Assert.DoesNotContain('\r', page.Text);
            Assert.EndsWith("\n", page.Text);
            Assert.DoesNotContain("\n\n\n", page.Text);
            Assert.DoesNotContain("\n\n", page.Text[^2..]);
        }
    }

    [Fact]
    public void Every_function_in_the_table_appears_with_an_anchor_and_an_example()
    {
        string text = Page("functions.md");

        foreach (RubedoFunctionSignature signature in RubedoFunctionTable.Signatures)
        {
            Assert.Contains($"<a id=\"fn-{signature.Name}\"></a>", text);
            Assert.Contains($"## {signature.Name}\n", text);
        }

        Assert.DoesNotContain("No worked example", text);
        Assert.Equal(RubedoFunctionTable.Signatures.Count, RubedoReferenceGenerator.GetExamples(Corpus).Count);
    }

    [Fact]
    public void Every_identifier_appears_with_its_type()
    {
        string text = Page("identifiers.md");

        foreach (var identifier in RubedoSchema.Standard.Identifiers)
        {
            string type = identifier.Value switch { RubedoType.Bool => "bool", RubedoType.Number => "number", _ => "text" };
            Assert.Contains($"`{identifier.Key}` | {type} |", text);
        }
    }

    [Fact]
    public void Every_widget_and_action_appears()
    {
        string widgets = Page("widgets.md");
        foreach (ViewWidget widget in Enum.GetValues<ViewWidget>())
        {
            Assert.Contains($"`{ViewWidgets.WireName(widget)}`", widgets);
        }

        string actions = Page("actions.md");
        foreach (string wire in _actionWireNames)
        {
            Assert.Contains($"`{wire}`", actions);
        }

        Assert.Equal(4, Enum.GetValues<ViewActionKind>().Length);
    }

    [Fact]
    public void Limits_come_from_the_engine_defaults()
    {
        string text = Page("limits.md");

        Assert.Contains("| Source length | 512 characters |", text);
        Assert.Contains("| Tokens | 256 |", text);
        Assert.Contains("| Nesting depth | 32 |", text);
        Assert.Contains("| Evaluation steps | 1000,", text);
        Assert.Contains("| String literal | 64 characters |", text);
        Assert.Contains("1000000000000000", text);
    }

    [Fact]
    public void Every_diagnostic_code_appears()
    {
        string text = Page("diagnostics.md");

        foreach (RubedoDiagnosticEntry entry in RubedoDiagnosticCatalog.Entries)
        {
            Assert.Contains($"`{entry.Code}`", text);
            Assert.False(string.IsNullOrWhiteSpace(entry.Explanation));
        }

        Assert.Equal(RubedoDiagnosticCatalog.Entries.Count, RubedoDiagnosticCatalog.Entries.Select(e => e.Code).Distinct().Count());
    }

    /// <summary>Scans the engine and analysis source for every code literal, so a new code without a documented explanation fails here.</summary>
    [Fact]
    public void Every_code_in_the_engine_source_is_documented()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        IEnumerable<string> files = _scannedProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (Match match in ExpressionCode().Matches(text))
            {
                found.Add(match.Groups[2].Value);
            }

            foreach (Match match in ViewCode().Matches(text))
            {
                found.Add(match.Groups[1].Value);
            }
        }

        var documented = RubedoDiagnosticCatalog.Entries.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(found);
        string[] undocumented = [.. found.Where(code => !documented.Contains(code))];
        string[] unused = [.. documented.Where(code => !found.Contains(code))];
        Assert.True(undocumented.Length == 0, "Codes in the engine source with no catalog entry: " + string.Join(", ", undocumented));
        Assert.True(unused.Length == 0, "Catalog entries for codes the engine source no longer has: " + string.Join(", ", unused));
    }

    [Fact]
    public void Every_worked_example_evaluates_as_documented()
    {
        IReadOnlyList<RubedoFunctionExample> examples = RubedoReferenceGenerator.GetExamples(Corpus);

        Assert.NotEmpty(examples);
        foreach (RubedoFunctionExample example in examples)
        {
            RubedoCompileResult compiled = RubedoEngine.Compile(example.Source);
            Assert.True(compiled.IsValid, $"{example.Function}: '{example.Source}' does not compile.");
            RubedoEvaluation evaluation = RubedoEngine.Evaluate(compiled.Program!, example.Context);
            Assert.Null(evaluation.Error);
            Assert.Equal(example.Value, evaluation.Value);
            Assert.Contains(example.Function + "(", example.Source);
        }
    }

    /// <summary>The CI drift check of doc 41d section 6: the committed reference must equal the regenerated one.</summary>
    [Fact]
    public void The_committed_reference_matches_the_generated_one()
    {
        string folder = RepoPaths.CommittedReference;
        var utf8 = new UTF8Encoding(false);
        var problems = new List<string>();
        var pages = RubedoReferenceGenerator.Generate();
        foreach (RubedoReferencePage page in pages)
        {
            string path = Path.Combine(folder, page.RelativePath);
            if (!File.Exists(path))
            {
                problems.Add($"missing: {page.RelativePath}");
            }
            else if (File.ReadAllText(path, utf8) != page.Text)
            {
                problems.Add($"differs: {page.RelativePath}");
            }
        }

        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (pages.All(p => p.RelativePath != relative))
                {
                    problems.Add($"unexpected: {relative}");
                }
            }
        }

        Assert.True(problems.Count == 0, "docs/reference/rubedo is stale; run 'dotnet run --project src/Bazaar.Rubedo.Docs.Tool -- --out docs/reference/rubedo'. " + string.Join("; ", problems));
    }

    [GeneratedRegex("(RubedoException|RubedoDiagnostic|Fail)\\(\"([a-z]+(?:-[a-z]+)*)\"")]
    private static partial Regex ExpressionCode();

    [GeneratedRegex("\"(view\\.[a-z_]+)\"")]
    private static partial Regex ViewCode();
}
