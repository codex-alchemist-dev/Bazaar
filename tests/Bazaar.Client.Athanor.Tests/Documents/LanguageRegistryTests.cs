namespace Bazaar.Client.Athanor.Tests.Documents;

public class LanguageRegistryTests
{
    private static readonly string[] _requiredLanguages =
    [
        "csharp", "fsharp", "typescript", "javascript", "json", "jsonc", "markdown", "yaml", "toml", "xml", "html",
        "css", "sql", "python", "rust", "go", "lua", "gdscript", "shell", "powershell", "dockerfile", "ini", "plaintext",
    ];

    public static TheoryData<string, string> Extensions => Rows(d => d.Extensions);

    public static TheoryData<string, string> FileNames => Rows(d => d.FileNames);

    public static TheoryData<string, string> Interpreters => Rows(d => d.Interpreters);

    private static TheoryData<string, string> Rows(Func<LanguageDefinition, IReadOnlyList<string>> keys)
    {
        var data = new TheoryData<string, string>();
        foreach (var definition in LanguageRegistry.Default.Definitions)
        {
            foreach (var key in keys(definition))
            {
                data.Add(key, definition.LanguageId);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public void Every_extension_row_resolves_to_its_language(string extension, string languageId)
    {
        Assert.Equal(languageId, LanguageRegistry.Default.Resolve("folder/file." + extension));
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public void Extensions_match_case_insensitively(string extension, string languageId)
    {
        Assert.Equal(languageId, LanguageRegistry.Default.Resolve("FILE." + extension.ToUpperInvariant()));
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void Every_well_known_file_name_resolves_to_its_language(string fileName, string languageId)
    {
        Assert.Equal(languageId, LanguageRegistry.Default.Resolve("build/" + fileName));
    }

    [Theory]
    [MemberData(nameof(Interpreters))]
    public void Every_interpreter_row_resolves_from_a_shebang(string interpreter, string languageId)
    {
        Assert.Equal(languageId, LanguageRegistry.Default.ResolveFirstLine("#!/usr/bin/" + interpreter));
    }

    [Fact]
    public void The_default_table_covers_every_required_language()
    {
        var ids = LanguageRegistry.Default.Definitions.Select(d => d.LanguageId).ToHashSet();

        foreach (var required in _requiredLanguages)
        {
            Assert.Contains(required, ids);
        }
    }

    [Theory]
    [InlineData("main.lua", "lua")]
    [InlineData("scripts/ui.luau", "lua")]
    [InlineData("Program.CS", "csharp")]
    [InlineData("App.Tsx", "typescript")]
    [InlineData("script.gd", "gdscript")]
    [InlineData("run.PS1", "powershell")]
    [InlineData("config.jsonc", "jsonc")]
    [InlineData("config.json", "json")]
    [InlineData("Project.csproj", "xml")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("services/api/Dockerfile", "dockerfile")]
    [InlineData("Containerfile", "dockerfile")]
    [InlineData("Makefile", "makefile")]
    [InlineData("GNUmakefile", "makefile")]
    [InlineData("makefile", "makefile")]
    public void Specific_names_and_extensions_resolve(string path, string expected)
    {
        Assert.Equal(expected, LanguageRegistry.Default.Resolve(path));
    }

    [Theory]
    [InlineData("README")]
    [InlineData("notes.unknownext")]
    [InlineData(".gitignore")]
    [InlineData("trailing.")]
    [InlineData("dir.v2/README")]
    public void Unknown_paths_default_to_plaintext(string path)
    {
        Assert.Equal("plaintext", LanguageRegistry.Default.Resolve(path));
    }

    [Theory]
    [InlineData("#!/usr/bin/env python3", "python")]
    [InlineData("#!/usr/bin/python3.12", "python")]
    [InlineData("#!/usr/bin/env -S node --experimental-vm", "javascript")]
    [InlineData("#!/usr/bin/env deno run", "javascript")]
    [InlineData("#!/bin/bash", "shell")]
    [InlineData("#!/bin/sh -e", "shell")]
    [InlineData("#!/usr/bin/env pwsh", "powershell")]
    [InlineData("#!/usr/bin/env luau", "lua")]
    [InlineData("#!/usr/bin/lua5.4", "lua")]
    [InlineData("\uFEFF#!/bin/sh", "shell")]
    [InlineData("  #!/bin/sh", "shell")]
    public void Shebang_selects_the_interpreter_language(string firstLine, string expected)
    {
        Assert.Equal(expected, LanguageRegistry.Default.ResolveFirstLine(firstLine));
        Assert.Equal(expected, LanguageRegistry.Default.Resolve("tools/run", firstLine));
    }

    [Theory]
    [InlineData("#!/usr/bin/unknown")]
    [InlineData("#!")]
    [InlineData("#!/usr/bin/env")]
    [InlineData("#!/usr/bin/env -S")]
    [InlineData("print('hi')")]
    [InlineData("")]
    public void Shebang_that_names_no_known_interpreter_gives_no_hint(string firstLine)
    {
        Assert.Null(LanguageRegistry.Default.ResolveFirstLine(firstLine));
        Assert.Equal("plaintext", LanguageRegistry.Default.Resolve("tools/run", firstLine));
    }

    [Fact]
    public void A_null_first_line_gives_no_hint()
    {
        Assert.Null(LanguageRegistry.Default.ResolveFirstLine(null));
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?>")]
    [InlineData("<?XML version=\"1.0\"?>")]
    [InlineData("\uFEFF<?xml version=\"1.0\"?>")]
    public void XML_declaration_selects_xml(string firstLine)
    {
        Assert.Equal("xml", LanguageRegistry.Default.Resolve("data/feed", firstLine));
    }

    [Fact]
    public void A_known_extension_wins_over_the_first_line()
    {
        Assert.Equal("csharp", LanguageRegistry.Default.Resolve("app.cs", "#!/bin/bash"));
    }

    [Fact]
    public void A_duplicate_extension_across_rows_is_rejected()
    {
        var rows = new[]
        {
            new LanguageDefinition("a", ["x"], [], []),
            new LanguageDefinition("b", ["X"], [], []),
        };

        Assert.Throws<ArgumentException>(() => new LanguageRegistry(rows));
    }

    [Fact]
    public void A_duplicate_file_name_in_one_row_is_rejected()
    {
        var rows = new[] { new LanguageDefinition("a", [], ["Makefile", "makefile"], []) };

        Assert.Throws<ArgumentException>(() => new LanguageRegistry(rows));
    }

    [Fact]
    public void The_registry_keeps_its_own_copy_of_the_table()
    {
        var rows = new List<LanguageDefinition> { new("a", ["x"], [], []) };
        var registry = new LanguageRegistry(rows);
        rows.Clear();

        Assert.Equal("a", registry.Resolve("f.x"));
        Assert.Single(registry.Definitions);
    }
}
