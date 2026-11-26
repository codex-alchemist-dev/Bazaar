using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Athanor.AvaloniaEdit.Tests;

public class ThemedSyntaxAndDiffTests
{
    private const string _code = "string s = \"hi\"; // note";
    private static readonly CancellationToken _none = CancellationToken.None;

    private static ThemeDefinition Theme(ThemeBase themeBase, string background, string keyword, string comment, string str, string def) =>
        new(
            SchemaVersion: 1,
            Name: "t",
            Base: themeBase,
            Tokens: new Dictionary<string, string>
            {
                ["color.background.editor"] = background,
                ["color.accent.default"] = "#89B4FA",
                ["color.syntax.default"] = def,
                ["color.syntax.keyword"] = keyword,
                ["color.syntax.string"] = str,
                ["color.syntax.comment"] = comment,
                ["color.syntax.number"] = "#FAB387",
                ["color.syntax.type"] = "#F9E2AF",
            });

    private static string Dark() => MonacoThemeGenerator.Generate(Theme(ThemeBase.Dark, "#181825", "#CBA6F7", "#6C7086", "#A6E3A1", "#CDD6F4"));

    private static string Light() => MonacoThemeGenerator.Generate(Theme(ThemeBase.Light, "#FFFFFF", "#0000FF", "#008000", "#A31515", "#000000"));

    private static EditorDocument Doc(string content) =>
        new(new EditorDocumentId("ws", "a.cs"), content, "rev-1", LanguageIds.CSharp, false);

    private static Color? ColourOf(AvaloniaEditHost host, string text, string token)
    {
        var document = new TextDocument(text);
        var highlighter = new DocumentHighlighter(document, host.Editor.SyntaxHighlighting!);
        HighlightedLine line = highlighter.HighlightLine(1);
        int offset = text.IndexOf(token, StringComparison.Ordinal);
        HighlightedSection? section = line.Sections.LastOrDefault(s => s.Offset <= offset && offset < s.Offset + s.Length);
        return section?.Color.Foreground?.GetBrush(null) is ISolidColorBrush brush ? brush.Color : null;
    }

    [Fact]
    public void Token_map_resolves_by_longest_prefix_and_ignores_the_default_rule()
    {
        var map = TokenColorMap.Parse(Dark());

        Assert.Equal(Color.Parse("#CBA6F7"), map.Resolve("keyword"));
        Assert.Equal(Color.Parse("#CBA6F7"), map.Resolve("keyword.control.flow"));
        Assert.Equal(Color.Parse("#CDD6F4"), map.Default);
        Assert.Null(map.Resolve("function"));
        Assert.Equal("comment", TokenColorMap.ScopeFor("XmlComment"));
        Assert.Equal("type", TokenColorMap.ScopeFor("ValueTypeKeywords"));
        Assert.Equal("string", TokenColorMap.ScopeFor("String"));
        Assert.Null(TokenColorMap.ScopeFor("Punctuation"));
    }

    [AvaloniaFact]
    public async Task Syntax_colours_come_from_the_theme_and_follow_a_live_swap()
    {
        await using var host = new AvaloniaEditHost();
        await host.OpenAsync(Doc(_code), _none);

        await host.SetThemeAsync(Dark(), _none);
        Color? darkString = ColourOf(host, _code, "\"hi\"");
        Color? darkComment = ColourOf(host, _code, "// note");
        await host.SetThemeAsync(Light(), _none);
        Color? lightString = ColourOf(host, _code, "\"hi\"");
        Color? lightComment = ColourOf(host, _code, "// note");

        Assert.Equal(Color.Parse("#A6E3A1"), darkString);
        Assert.Equal(Color.Parse("#6C7086"), darkComment);
        Assert.Equal(Color.Parse("#A31515"), lightString);
        Assert.Equal(Color.Parse("#008000"), lightComment);
    }

    [AvaloniaFact]
    public async Task Theming_does_not_mutate_the_shipped_definition()
    {
        await using var host = new AvaloniaEditHost();
        await host.OpenAsync(Doc("// c"), _none);
        await host.SetThemeAsync(Dark(), _none);
        IHighlightingDefinition shipped = HighlightingManager.Instance.GetDefinitionByExtension(".cs")!;

        Assert.NotSame(shipped, host.Editor.SyntaxHighlighting);
        var shippedComment = new DocumentHighlighter(new TextDocument("// c"), shipped).HighlightLine(1);
        Assert.NotEqual(Color.Parse("#6C7086"), (shippedComment.Sections[0].Color.Foreground?.GetBrush(null) as ISolidColorBrush)?.Color);
    }

    [Fact]
    public void Line_diff_marks_added_removed_and_changed_lines_aligned()
    {
        IReadOnlyList<DiffRow> rows = LineDiff.Compute("a\nb\nc\nd\n", "a\nB\nc\nd\ne\n");

        Assert.Equal(
            [
                new DiffRow("a", "a", DiffRowKind.Equal),
                new DiffRow("b", "B", DiffRowKind.Changed),
                new DiffRow("c", "c", DiffRowKind.Equal),
                new DiffRow("d", "d", DiffRowKind.Equal),
                new DiffRow(null, "e", DiffRowKind.Added),
            ],
            rows);
        Assert.Equal([new DiffRow("x", null, DiffRowKind.Removed)], LineDiff.Compute("x", string.Empty));
        Assert.Empty(LineDiff.Compute(string.Empty, string.Empty));
        Assert.All(LineDiff.Compute("a\r\nb", "a\nb"), r => Assert.Equal(DiffRowKind.Equal, r.Kind));
    }

    [Fact]
    public void Line_diff_finds_a_removed_line_in_the_middle()
    {
        IReadOnlyList<DiffRow> rows = LineDiff.Compute("1\n2\n3\n4\n5", "1\n2\n4\n5");

        Assert.Equal("3", rows.Single(r => r.Kind == DiffRowKind.Removed).Left);
        Assert.Equal(4, rows.Count(r => r.Kind == DiffRowKind.Equal));
    }

    [Fact]
    public void Diff_colours_use_theme_values_or_blend_over_the_background()
    {
        DiffColors blended = DiffColors.Parse(Dark());
        DiffColors light = DiffColors.Parse(Light());
        string explicitJson = """{"base":"vs","colors":{"editor.background":"#FFFFFF","diffEditor.insertedLineBackground":"#00FF00FF","diffEditor.removedLineBackground":"#FF0000FF"}}""";
        DiffColors explicitColours = DiffColors.Parse(explicitJson);

        Assert.True(blended.Added.G > blended.Added.R);
        Assert.True(blended.Removed.R > blended.Removed.G);
        Assert.NotEqual(blended.Added, light.Added);
        Assert.Equal(Color.Parse("#00FF00"), explicitColours.Added);
        Assert.Equal(Color.Parse("#FF0000"), explicitColours.Removed);
    }

    [AvaloniaFact]
    public async Task Show_diff_fills_both_sides_with_equal_lines_and_theme_colours()
    {
        await using var host = new AvaloniaEditHost();
        await host.SetThemeAsync(Dark(), _none);

        await host.ShowDiffAsync("a\nb\nc", "a\nB\nc\nd", LanguageIds.PlainText, _none);

        Assert.True(host.IsDiffVisible);
        Assert.False(host.Editor.IsVisible);
        Assert.Equal("a\nb\nc\n", host.Diff.Left.Text);
        Assert.Equal("a\nB\nc\nd", host.Diff.Right.Text);
        Assert.Equal(host.Diff.Left.Document.LineCount, host.Diff.Right.Document.LineCount);
        Assert.Equal(DiffRowKind.Changed, host.Diff.KindAt(1));
        Assert.Equal(DiffRowKind.Added, host.Diff.KindAt(3));
        Assert.Equal(DiffColors.Parse(Dark()), host.Diff.Colors);
        Assert.True(host.Diff.Left.IsReadOnly && host.Diff.Right.IsReadOnly);

        host.CloseDiff();
        Assert.True(host.Editor.IsVisible);
        Assert.False(host.IsDiffVisible);
    }

    [AvaloniaFact]
    public async Task A_theme_swap_recolours_the_diff()
    {
        await using var host = new AvaloniaEditHost();
        await host.SetThemeAsync(Dark(), _none);
        await host.ShowDiffAsync("a", "b", LanguageIds.PlainText, _none);
        DiffColors dark = host.Diff.Colors;

        await host.SetThemeAsync(Light(), _none);

        Assert.NotEqual(dark, host.Diff.Colors);
        Assert.Equal(DiffColors.Parse(Light()), host.Diff.Colors);
    }

    [AvaloniaFact]
    public async Task Diff_scroll_is_synchronised_in_both_directions()
    {
        await using var host = new AvaloniaEditHost();
        string many = string.Join('\n', Enumerable.Range(0, 400).Select(i => "line " + i));
        await host.ShowDiffAsync(many, many + "\nextra", LanguageIds.PlainText, _none);
        var window = new Window { Width = 600, Height = 300, Content = host.Root };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        host.Diff.ScrollTo(new Vector(0, 200));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(200, host.Diff.LeftOffset.Y);
        Assert.Equal(200, host.Diff.RightOffset.Y);

        ((Avalonia.Controls.Primitives.IScrollable)host.Diff.Right.TextArea.TextView).Offset = new Vector(0, 120);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(120, host.Diff.LeftOffset.Y);
        Assert.Equal(120, host.Diff.RightOffset.Y);
        window.Close();
    }
}
