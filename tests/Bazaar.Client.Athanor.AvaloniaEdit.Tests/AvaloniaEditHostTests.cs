using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Athanor.AvaloniaEdit.Tests;

public class AvaloniaEditHostTests
{
    private static readonly CancellationToken _none = CancellationToken.None;

    private static EditorDocument Doc(string path, string content = "hello", string language = LanguageIds.PlainText, bool readOnly = false) =>
        new(new EditorDocumentId("ws", path), content, "rev-1", language, readOnly);

    private static ThemeDefinition DarkTheme() =>
        new(
            SchemaVersion: 1,
            Name: "Test dark",
            Base: ThemeBase.Dark,
            Tokens: new Dictionary<string, string>
            {
                ["color.background.editor"] = "#181825",
                ["color.accent.default"] = "#89B4FA",
                ["color.syntax.default"] = "#CDD6F4",
                ["color.syntax.keyword"] = "#CBA6F7",
                ["color.syntax.string"] = "#A6E3A1",
                ["color.syntax.comment"] = "#6C7086",
                ["color.syntax.number"] = "#FAB387",
                ["color.syntax.type"] = "#F9E2AF",
            });

    [AvaloniaFact]
    public async Task Open_loads_content_and_raises_ready_once()
    {
        await using var host = new AvaloniaEditHost();
        int ready = 0;
        host.Ready += (_, _) => ready++;

        await host.OpenAsync(Doc("a.txt", "line one\nline two"), _none);
        await host.OpenAsync(Doc("b.txt", "other"), _none);

        Assert.Equal(1, ready);
        Assert.Equal(new EditorDocumentId("ws", "b.txt"), host.ActiveDocument);
        Assert.Equal("other", host.Editor.Document.Text);
        Assert.Equal("line one\nline two", host.GetContent(new EditorDocumentId("ws", "a.txt")));
    }

    [AvaloniaFact]
    public async Task Programmatic_open_does_not_raise_changed_but_edits_do_with_rising_versions()
    {
        await using var host = new AvaloniaEditHost();
        var changes = new List<EditorChange>();
        host.Changed += (_, c) => changes.Add(c);
        EditorDocument document = Doc("a.txt", "abc");

        await host.OpenAsync(document, _none);
        Assert.Empty(changes);

        host.Editor.Document.Insert(3, "d");
        host.Editor.Document.Insert(4, "e");

        Assert.Equal([1L, 2L], changes.Select(c => c.Version));
        Assert.Equal("abcde", changes[^1].Content);
        Assert.All(changes, c => Assert.True(c.Dirty));
        Assert.True(host.IsDirty(document.Id));
    }

    [AvaloniaFact]
    public async Task Edit_back_to_the_opened_content_is_clean_and_versions_survive_reopen()
    {
        await using var host = new AvaloniaEditHost();
        var changes = new List<EditorChange>();
        host.Changed += (_, c) => changes.Add(c);
        EditorDocument document = Doc("a.txt", "abc");
        await host.OpenAsync(document, _none);

        host.Editor.Document.Insert(3, "d");
        host.Editor.Document.Remove(3, 1);
        Assert.False(changes[^1].Dirty);

        await host.CloseAsync(document.Id, _none);
        await host.OpenAsync(document, _none);
        host.Editor.Document.Insert(0, "x");

        Assert.Equal(3, changes[^1].Version);
    }

    [AvaloniaFact]
    public async Task Read_only_is_applied_to_the_shown_document_and_follows_switching()
    {
        await using var host = new AvaloniaEditHost();
        EditorDocument locked = Doc("a.txt", readOnly: true);
        EditorDocument open = Doc("b.txt");
        await host.OpenAsync(locked, _none);
        Assert.True(host.Editor.IsReadOnly);

        await host.OpenAsync(open, _none);
        Assert.False(host.Editor.IsReadOnly);

        await host.SetReadOnlyAsync(open.Id, true, _none);
        Assert.True(host.Editor.IsReadOnly);

        await host.CloseAsync(open.Id, _none);
        Assert.Equal(locked.Id, host.ActiveDocument);
        Assert.True(host.Editor.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task Closing_the_last_document_leaves_an_empty_editable_surface()
    {
        await using var host = new AvaloniaEditHost();
        EditorDocument document = Doc("a.txt", "x");
        await host.OpenAsync(document, _none);

        await host.CloseAsync(document.Id, _none);

        Assert.Null(host.ActiveDocument);
        Assert.Equal(string.Empty, host.Editor.Document.Text);
        Assert.Throws<InvalidOperationException>(() => host.GetContent(document.Id));
    }

    [AvaloniaTheory]
    [InlineData(LanguageIds.CSharp)]
    [InlineData(LanguageIds.Xml)]
    [InlineData(LanguageIds.Json)]
    [InlineData(LanguageIds.JavaScript)]
    [InlineData(LanguageIds.Html)]
    [InlineData(LanguageIds.Css)]
    [InlineData(LanguageIds.Python)]
    [InlineData(LanguageIds.Markdown)]
    [InlineData(LanguageIds.Sql)]
    [InlineData(LanguageIds.PowerShell)]
    public async Task Shipped_languages_get_a_highlighting_definition(string languageId)
    {
        await using var host = new AvaloniaEditHost();

        await host.OpenAsync(Doc("f", "x", languageId), _none);

        Assert.NotNull(host.Editor.SyntaxHighlighting);
        Assert.Equal(languageId, host.ActiveLanguageId);
    }

    [AvaloniaTheory]
    [InlineData(LanguageIds.PlainText)]
    [InlineData(LanguageIds.Rust)]
    [InlineData("nonsense")]
    public async Task Languages_without_a_definition_are_plain(string languageId)
    {
        await using var host = new AvaloniaEditHost();

        await host.OpenAsync(Doc("f", "x", languageId), _none);

        Assert.Null(host.Editor.SyntaxHighlighting);
    }

    [AvaloniaFact]
    public async Task Language_follows_the_registry_and_can_change_after_open()
    {
        await using var host = new AvaloniaEditHost();
        EditorDocument document = Doc("Program.cs", "class A {}", LanguageRegistry.Default.Resolve("src/Program.cs"));
        await host.OpenAsync(document, _none);
        Assert.NotNull(host.Editor.SyntaxHighlighting);

        await host.SetLanguageAsync(document.Id, LanguageIds.PlainText, _none);

        Assert.Null(host.Editor.SyntaxHighlighting);
    }

    [AvaloniaFact]
    public async Task Theme_json_from_the_provider_sets_editor_colours()
    {
        await using var host = new AvaloniaEditHost();
        var provider = new EditorThemeProvider(DarkTheme);

        await provider.PushAsync(host, _none);

        Assert.Equal(Color.Parse("#181825"), ((ISolidColorBrush)host.Editor.Background!).Color);
        Assert.Equal(Color.Parse("#CDD6F4"), ((ISolidColorBrush)host.Editor.Foreground!).Color);
        Assert.Equal(Color.Parse("#89B4FA"), host.Colors!.Caret);
    }

    [AvaloniaFact]
    public async Task Malformed_theme_faults_the_task_and_keeps_the_old_colours()
    {
        await using var host = new AvaloniaEditHost();
        await host.SetThemeAsync("{\"colors\":{\"editor.background\":\"#101010\"}}", _none);

        await Assert.ThrowsAsync<FormatException>(() => host.SetThemeAsync("not json", _none));

        Assert.Equal(Color.Parse("#101010"), host.Colors!.Background);
    }

    [AvaloniaFact]
    public async Task Caret_reports_line_and_column_and_goto_clamps()
    {
        await using var host = new AvaloniaEditHost();
        await host.OpenAsync(Doc("a.txt", "one\ntwo three\nfour"), _none);
        var moves = new List<EditorCaret>();
        host.CaretMoved += (_, c) => moves.Add(c);

        host.GoTo(2, 5);
        Assert.Equal(new EditorCaret(2, 5), host.Caret);

        host.GoTo(99, 99);
        Assert.Equal(new EditorCaret(3, 5), host.Caret);

        host.GoTo(0, 0);
        Assert.Equal(new EditorCaret(1, 1), host.Caret);
        Assert.NotEmpty(moves);
    }

    [AvaloniaFact]
    public async Task Request_save_raises_for_the_shown_document_only()
    {
        await using var host = new AvaloniaEditHost();
        var saves = new List<EditorDocumentId>();
        host.SaveRequested += (_, id) => saves.Add(id);

        host.RequestSave();
        EditorDocument document = Doc("a.txt");
        await host.OpenAsync(document, _none);
        host.RequestSave();

        Assert.Equal([document.Id], saves);
    }

    [AvaloniaFact]
    public async Task Claimed_key_is_handled_and_not_raised_unclaimed_key_is_raised_and_left_alone()
    {
        var handoff = new RouterKeyHandoff(key => key.Key == "M");
        await using var host = new AvaloniaEditHost(handoff);
        var seen = new List<EditorKeyEvent>();
        host.KeyPressed += (_, k) => seen.Add(k);

        KeyEventArgs claimed = Press(host, Key.M, KeyModifiers.None);
        KeyEventArgs free = Press(host, Key.A, KeyModifiers.Shift);

        Assert.True(claimed.Handled);
        Assert.False(free.Handled);
        EditorKeyEvent only = Assert.Single(seen);
        Assert.Equal("A", only.Key);
        Assert.True(only.Shift);
    }

    [AvaloniaFact]
    public async Task Ctrl_keys_stay_with_the_editor()
    {
        var handoff = new RouterKeyHandoff(_ => true);
        await using var host = new AvaloniaEditHost(handoff);

        KeyEventArgs ctrl = Press(host, Key.S, KeyModifiers.Control);

        Assert.False(ctrl.Handled);
    }

    [AvaloniaFact]
    public async Task Diff_is_not_supported_and_crashed_never_fires()
    {
        await using var host = new AvaloniaEditHost();
        bool crashed = false;
        host.Crashed += (_, _) => crashed = true;

        await Assert.ThrowsAsync<NotSupportedException>(() => host.ShowDiffAsync("a", "b", LanguageIds.PlainText, _none));

        Assert.False(AvaloniaEditHost.SupportsDiff);
        Assert.False(crashed);
    }

    [AvaloniaFact]
    public async Task Cancelled_token_cancels_and_disposed_host_refuses_work()
    {
        var host = new AvaloniaEditHost();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.OpenAsync(Doc("a.txt"), cts.Token));

        await host.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.OpenAsync(Doc("a.txt"), _none));
    }

    private static KeyEventArgs Press(AvaloniaEditHost host, Key key, KeyModifiers modifiers)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = host.Editor,
        };
        host.Editor.RaiseEvent(args);
        return args;
    }
}
