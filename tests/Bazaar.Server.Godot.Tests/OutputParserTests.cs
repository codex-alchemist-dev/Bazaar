using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot.Tests;

public class OutputParserTests
{
    [Fact]
    public void Empty_output_yields_nothing()
    {
        Assert.Empty(GodotOutputParser.Parse(string.Empty));
    }

    [Fact]
    public void Script_error_takes_file_and_line_from_the_at_line()
    {
        const string output = "SCRIPT ERROR: Parse Error: Identifier \"x\" not declared in the current scope.\n          at: GDScript::reload (res://scripts/main.gd:12)\n";

        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse(output));

        Assert.Equal(GodotDiagnosticSeverity.Error, d.Severity);
        Assert.Equal(GodotDiagnosticSource.Script, d.Source);
        Assert.Equal("scripts/main.gd", d.File);
        Assert.Equal(12, d.Line);
        Assert.Contains("not declared", d.Message);
    }

    [Fact]
    public void Engine_error_with_a_cpp_location_keeps_it_as_printed()
    {
        const string output = "ERROR: Cannot open file 'res://missing.tscn'.\n   at: load (core/io/resource_loader.cpp:303)\n";

        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse(output));

        Assert.Equal(GodotDiagnosticSource.Engine, d.Source);
        Assert.Equal("core/io/resource_loader.cpp", d.File);
        Assert.Equal(303, d.Line);
    }

    [Fact]
    public void Error_with_an_inline_res_location_is_parsed()
    {
        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse("ERROR: res://scripts/a.gd:7 - Parse Error: bad token\n"));

        Assert.Equal("scripts/a.gd", d.File);
        Assert.Equal(7, d.Line);
    }

    [Fact]
    public void Error_without_a_location_has_empty_file_and_line_zero()
    {
        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse("ERROR: Something went wrong\n"));

        Assert.Equal(string.Empty, d.File);
        Assert.Equal(0, d.Line);
    }

    [Fact]
    public void Warning_lines_are_warnings()
    {
        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse("WARNING: Deprecated thing\n     at: foo (scene/main/node.cpp:10)\n"));

        Assert.Equal(GodotDiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(10, d.Line);
    }

    [Fact]
    public void Script_warning_is_a_script_warning()
    {
        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse("SCRIPT WARNING: unused variable\n"));

        Assert.Equal(GodotDiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(GodotDiagnosticSource.Script, d.Source);
    }

    [Fact]
    public void Only_the_first_at_line_is_used()
    {
        const string output = "SCRIPT ERROR: boom\n   at: first (res://a.gd:1)\n   at: second (res://b.gd:2)\n";

        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse(output));

        Assert.Equal("a.gd", d.File);
        Assert.Equal(1, d.Line);
    }

    [Fact]
    public void Multiple_errors_each_get_their_own_location()
    {
        const string output = "SCRIPT ERROR: one\n   at: x (res://a.gd:1)\nSCRIPT ERROR: two\n   at: y (res://b.gd:2)\n";

        IReadOnlyList<GodotDiagnostic> found = GodotOutputParser.Parse(output);

        Assert.Equal(2, found.Count);
        Assert.Equal("b.gd", found[1].File);
    }

    [Fact]
    public void Ansi_colour_codes_are_removed_and_crlf_is_handled()
    {
        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse("\u001b[1;31mERROR:\u001b[0m bad thing\r\n"));

        // The colour code sits inside the prefix here, so the line must still be recognised after stripping.
        Assert.Equal("bad thing", d.Message);
    }

    [Fact]
    public void Unrelated_lines_are_ignored()
    {
        Assert.Empty(GodotOutputParser.Parse("Godot Engine v4.7.2.stable.official\nImporting assets\nERRORS are not lines\n"));
    }

    [Fact]
    public void An_at_line_with_no_location_is_skipped_without_failing()
    {
        GodotDiagnostic d = Assert.Single(GodotOutputParser.Parse("ERROR: x\n   at: no location here\n"));

        Assert.Equal(0, d.Line);
    }

    [Fact]
    public void Count_and_message_length_are_bounded()
    {
        string many = string.Concat(Enumerable.Repeat("ERROR: e\n", GodotOutputParser.MaxDiagnostics + 50));
        string longLine = "ERROR: " + new string('a', GodotOutputParser.MaxMessageLength + 100) + "\n";

        Assert.Equal(GodotOutputParser.MaxDiagnostics, GodotOutputParser.Parse(many).Count);
        Assert.Equal(GodotOutputParser.MaxMessageLength, Assert.Single(GodotOutputParser.Parse(longLine)).Message.Length);
    }

    [Fact]
    public void Output_cap_cuts_and_marks()
    {
        string cut = GodotOutputCap.Apply(new string('x', 20), 10, out bool truncated);
        string same = GodotOutputCap.Apply("short", 10, out bool notTruncated);

        Assert.True(truncated);
        Assert.Contains("[output truncated at 10 characters]", cut);
        Assert.False(notTruncated);
        Assert.Equal("short", same);
    }
}
