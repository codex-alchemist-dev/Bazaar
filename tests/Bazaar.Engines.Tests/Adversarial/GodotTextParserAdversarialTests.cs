using System.Diagnostics;
using Bazaar.Engines.Godot.SceneDiff;

namespace Bazaar.Engines.Tests.Adversarial;

/// <summary>A hostile .tscn or .tres: huge, deeply nested, unterminated or pathological. Each parse must end fast, with a parse error or a result.</summary>
public class GodotTextParserAdversarialTests
{
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(2);

    private static void CompletesQuickly(string text)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            SceneDiff.Compare(text, text);
        }
        catch (SceneParseException)
        {
            // A refusal is a fine answer; hanging or crashing is not.
        }

        Assert.True(watch.Elapsed < _budget, $"took {watch.Elapsed}");
    }

    [Fact]
    public void Deeply_nested_brackets_are_refused_not_recursed_into()
    {
        string text = "[gd_scene]\n[node name=\"A\" type=\"Node\"]\nv = " + new string('[', 200_000) + new string(']', 200_000) + "\n";

        Assert.Throws<SceneParseException>(() => SceneDiff.Compare(text, text));
        CompletesQuickly(text);
    }

    [Fact]
    public void Deep_nesting_in_a_header_attribute_is_refused()
    {
        string text = "[gd_scene]\n[node name=\"A\" type=" + new string('(', 100_000) + new string(')', 100_000) + "]\n";

        Assert.Throws<SceneParseException>(() => SceneDiff.Compare(text, text));
    }

    [Fact]
    public void Nesting_at_a_realistic_depth_still_parses()
    {
        string text = "[gd_scene]\n[node name=\"A\" type=\"Node\"]\nv = " + new string('[', 50) + new string(']', 50) + "\n";

        Assert.Empty(SceneDiff.Compare(text, text));
    }

    [Fact]
    public void A_single_line_of_fifty_megabytes_is_refused_quickly()
    {
        string text = "[gd_scene]\n[node name=\"A\" type=\"Node\"]\nv = \"" + new string('a', 50 * 1024 * 1024) + "\"\n";
        var watch = Stopwatch.StartNew();

        Assert.Throws<SceneParseException>(() => SceneDiff.Compare(text, text));

        Assert.True(watch.Elapsed < _budget, $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_file_larger_than_the_cap_is_refused()
    {
        string big = new string(';', GodotTextParser.MaxTextChars + 1);

        Assert.Throws<SceneParseException>(() => SceneDiff.Compare(big, "[gd_scene]\n"));
    }

    [Fact]
    public void An_unterminated_string_is_refused_quickly()
    {
        CompletesQuickly("[gd_scene]\n[node name=\"A\" type=\"Node\"]\nv = \"" + new string('a', 200_000));
        Assert.Throws<SceneParseException>(() => SceneDiff.Compare("[gd_scene]\n[node name=\"A\"]\nv = \"abc\n", "[gd_scene]\n"));
    }

    [Fact]
    public void An_unterminated_bracket_over_many_lines_is_linear()
    {
        string text = "[gd_scene]\n[node name=\"A\" type=\"Node\"]\nv = [\n" + string.Concat(Enumerable.Repeat("1,\n", 70_000));

        CompletesQuickly(text);
        Assert.Throws<SceneParseException>(() => SceneDiff.Compare(text, text));
    }

    [Fact]
    public void Nul_bytes_do_not_break_the_parser()
    {
        CompletesQuickly("[gd_scene]\n[node name=\"A\0\" type=\"No\0de\"]\nv\0 = \0\n\0\0\0");
    }

    [Fact]
    public void Huge_numeric_literals_are_kept_as_text_and_never_parsed()
    {
        string text = "[gd_scene]\n[node name=\"A\" type=\"Node\"]\nv = " + new string('9', 150_000) + "e" + new string('9', 50_000) + "\n";

        Assert.Empty(SceneDiff.Compare(text, text));
        CompletesQuickly(text);
    }

    [Fact]
    public void Many_small_sections_and_many_properties_are_linear()
    {
        string sections = "[gd_scene]\n" + string.Concat(Enumerable.Range(0, 10_000).Select(i => $"[node name=\"n{i}\" type=\"Node\" parent=\".\"]\nv = {i}\n"));
        string properties = "[gd_scene]\n[node name=\"A\" type=\"Node\"]\n" + string.Concat(Enumerable.Range(0, 12_000).Select(i => $"p{i} = {i}\n"));

        CompletesQuickly(sections);
        CompletesQuickly(properties);
    }

    [Fact]
    public void A_header_with_thousands_of_attributes_is_linear()
    {
        CompletesQuickly("[gd_scene]\n[node name=\"A\" " + string.Join(' ', Enumerable.Range(0, 15_000).Select(i => $"a{i}=\"{i}\"")) + "]\n");
    }

    [Fact]
    public void Alternating_quote_and_escape_patterns_are_linear()
    {
        CompletesQuickly("[gd_scene]\n[node name=\"A\" type=\"Node\"]\nv = " + string.Concat(Enumerable.Repeat("\"\\\\\\\"", 40_000)) + "\n");
    }
}
