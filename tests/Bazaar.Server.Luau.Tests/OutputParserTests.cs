using Bazaar.Server.Luau;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau.Tests;

/// <summary>Parser tests on recorded text fixtures, pass and fail, for each tool. No binary is run.</summary>
public class OutputParserTests
{
    // Recorded-shape fixtures. They follow the assumed formats documented on each parser; re-record them when the real tools are pinned.
    private const string _analyzeFail = """
        scripts/Main.lua(3,15-21): TypeError: Type 'string' could not be converted into 'number'
        scripts/Main.lua(9,1): UnknownGlobal: Unknown global 'gettable'
        scripts/Util.lua(4,7): LintUnusedVariable: Variable 'tmp' is never used
        """;

    private const string _analyzeGnu = "scripts/Main.lua:12:4: error: Expected 'end' when closing function\n";

    private const string _seleneFail = """
        {"code":"unused_variable","level":"warning","message":"local `tmp` is never used","primary_label":{"filename":"scripts/Util.lua","span":{"start_line":4,"start_column":7,"end_line":4,"end_column":10},"message":""},"notes":[],"help":null}
        {"code":"undefined_variable","level":"error","message":"undefined variable `gettable`","primary_label":{"filename":"scripts/Main.lua","span":{"start_line":9,"start_column":1,"end_line":9,"end_column":9},"message":""},"notes":[],"help":null}
        """;

    private const string _styluaFail = """
        Diff in scripts/Main.lua at line 3:
        -local x=1
        +local x = 1
        Diff in scripts/Util.lua:
        -return  tmp
        +return tmp
        """;

    [Fact]
    public void Analyze_empty_output_is_clean()
    {
        Assert.Empty(LuauAnalyzeOutputParser.Parse(string.Empty));
    }

    [Fact]
    public void Analyze_parses_type_errors_and_lint_warnings()
    {
        IReadOnlyList<LuauDiagnostic> found = LuauAnalyzeOutputParser.Parse(_analyzeFail);

        Assert.Equal(3, found.Count);
        Assert.Equal(new LuauDiagnostic(LuauToolKind.Analyze, "scripts/Main.lua", 3, 15, LuauSeverity.Error, "TypeError", "Type 'string' could not be converted into 'number'"), found[0]);
        Assert.Equal(LuauSeverity.Error, found[1].Severity);
        Assert.Equal("UnknownGlobal", found[1].Code);
        Assert.Equal(LuauSeverity.Warning, found[2].Severity);
        Assert.Equal("LintUnusedVariable", found[2].Code);
    }

    [Fact]
    public void Analyze_accepts_gnu_style_lines()
    {
        LuauDiagnostic only = Assert.Single(LuauAnalyzeOutputParser.Parse(_analyzeGnu));

        Assert.Equal("scripts/Main.lua", only.File);
        Assert.Equal(12, only.Line);
        Assert.Equal(4, only.Column);
        Assert.Equal(LuauSeverity.Error, only.Severity);
    }

    [Fact]
    public void Analyze_skips_summary_and_blank_lines()
    {
        Assert.Empty(LuauAnalyzeOutputParser.Parse("\nDone checking 4 files\n\n"));
    }

    [Fact]
    public void Selene_empty_output_is_clean()
    {
        Assert.Empty(SeleneOutputParser.Parse(string.Empty));
    }

    [Fact]
    public void Selene_parses_json_lines_with_level_and_position()
    {
        IReadOnlyList<LuauDiagnostic> found = SeleneOutputParser.Parse(_seleneFail);

        Assert.Equal(2, found.Count);
        Assert.Equal(new LuauDiagnostic(LuauToolKind.Selene, "scripts/Util.lua", 4, 7, LuauSeverity.Warning, "unused_variable", "local `tmp` is never used"), found[0]);
        Assert.Equal(LuauSeverity.Error, found[1].Severity);
        Assert.Equal("scripts/Main.lua", found[1].File);
    }

    [Fact]
    public void Selene_skips_non_json_and_malformed_lines()
    {
        const string noise = "Results:\n{not json}\n{\"code\":\"x\"}\n[]\n";

        Assert.Empty(SeleneOutputParser.Parse(noise));
    }

    [Fact]
    public void Selene_maps_unknown_level_to_info()
    {
        const string line = "{\"code\":\"allow_thing\",\"level\":\"allow\",\"message\":\"m\",\"primary_label\":{\"filename\":\"a.lua\",\"span\":{\"start_line\":1,\"start_column\":2}}}";

        Assert.Equal(LuauSeverity.Info, Assert.Single(SeleneOutputParser.Parse(line)).Severity);
    }

    [Fact]
    public void StyLua_clean_output_is_empty()
    {
        Assert.Empty(StyLuaOutputParser.Parse(string.Empty));
    }

    [Fact]
    public void StyLua_reports_one_finding_per_diff_header()
    {
        IReadOnlyList<LuauDiagnostic> found = StyLuaOutputParser.Parse(_styluaFail);

        Assert.Equal(2, found.Count);
        Assert.Equal(new LuauDiagnostic(LuauToolKind.StyLuaCheck, "scripts/Main.lua", 3, 0, LuauSeverity.Warning, "format", "File differs from StyLua's formatting."), found[0]);
        Assert.Equal("scripts/Util.lua", found[1].File);
        Assert.Equal(0, found[1].Line);
    }
}
