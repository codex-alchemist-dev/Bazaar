using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.GitGui.Diff;
using Bazaar.Client.GitGui.Panes;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.Tests;

public sealed class HeadlessRenderTests
{
    private static List<string> Texts(Control root)
    {
        var window = new Window { Width = 900, Height = 700, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var texts = new List<string>();
        foreach (var visual in window.GetVisualDescendants())
        {
            if (visual is TextBlock { Text: { Length: > 0 } text })
            {
                texts.Add(text);
            }
        }

        return texts;
    }

    private static GitPrFileDto File(string? patch, string status = "modified", bool truncated = false) =>
        new("src/a.cs", status, 1, 1, patch, truncated);

    [AvaloniaFact]
    public void Native_surface_renders_numbers_markers_and_text()
    {
        var model = DiffParser.Parse(File("@@ -1,2 +1,2 @@\n keep\n-old\n+new\n"));
        var surface = new NativeDiffSurface();

        var texts = Texts(surface.Create(model));

        Assert.Contains("src/a.cs", texts);
        Assert.Contains("@@ -1,2 +1,2 @@", texts);
        Assert.Contains("keep", texts);
        Assert.Contains("old", texts);
        Assert.Contains("new", texts);
        Assert.Contains("+", texts);
        Assert.Contains("-", texts);
        Assert.Contains("2", texts);
    }

    [AvaloniaFact]
    public void Native_surface_virtualises_a_long_patch()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 5000).Select(i => "+line " + i));
        var model = DiffParser.Parse(File("@@ -0,0 +1,5000 @@\n" + lines + "\n"));

        var control = new NativeDiffSurface().Create(model);
        var texts = Texts(control);

        var list = control.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(5001, ((System.Collections.ICollection)list.ItemsSource!).Count);
        Assert.True(texts.Count < 1000, $"built {texts.Count} text blocks for 5001 rows");
    }

    [AvaloniaFact]
    public void Native_surface_shows_the_truncated_notice()
    {
        var texts = Texts(new NativeDiffSurface().Create(DiffParser.Parse(File("@@ -1 +1 @@\n-a\n+b\n", truncated: true))));
        Assert.Contains(texts, t => t.Contains("cut at the server size limit", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Native_surface_shows_binary_and_no_patch_notices()
    {
        var binary = Texts(new NativeDiffSurface().Create(DiffParser.Parse(File("Binary files a/x and b/x differ\n"))));
        Assert.Contains(binary, t => t.StartsWith("Binary file", StringComparison.Ordinal));

        var none = Texts(new NativeDiffSurface().Create(DiffParser.Parse(File(null))));
        Assert.Contains("No patch to show for this file.", none);

        var renamed = Texts(new NativeDiffSurface().Create(DiffParser.Parse(File(null, "renamed"))));
        Assert.Contains("Renamed with no content changes.", renamed);
    }

    [AvaloniaFact]
    public void Native_surface_takes_added_line_colour_from_the_theme_token()
    {
        var added = new SolidColorBrush(Color.Parse("#00AA00"));
        Application.Current!.Resources["Bazaar.brush.color.syntax.string"] = added;
        try
        {
            var control = new NativeDiffSurface().Create(DiffParser.Parse(File("@@ -0,0 +1 @@\n+green\n")));
            Texts(control);

            var cell = control.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "green");
            Assert.Same(added, cell.Foreground);
        }
        finally
        {
            Application.Current!.Resources.Remove("Bazaar.brush.color.syntax.string");
        }
    }

    [AvaloniaFact]
    public async Task Prs_pane_renders_rows_banner_and_provider_noun()
    {
        var client = Fixtures.ClientWith(3);
        client.Stale = true;
        var registry = Fixtures.Registry(client, null, new FakeProviderUi("github", "pull request"));
        var pane = (GitPrsPaneViewModel)registry.Create(Fixtures.Descriptor("git.prs", """{"provider":"github","repo":"acme/widgets"}"""));
        await pane.List.LoadAsync(TestContext.Current.CancellationToken);

        var texts = Texts(registry.CreateView(pane));

        Assert.Contains(texts, t => t.StartsWith("#3 PR 3", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("last saved list of pull requests", StringComparison.Ordinal));
        Assert.Contains("Page 1", texts);
    }

    [AvaloniaFact]
    public void Prs_pane_loads_itself_when_it_reaches_the_screen()
    {
        var registry = Fixtures.Registry(Fixtures.ClientWith(2));
        var pane = registry.Create(Fixtures.Descriptor("git.prs", """{"provider":"github","repo":"acme/widgets"}"""));

        var texts = Texts(registry.CreateView(pane));

        Assert.Contains(texts, t => t.StartsWith("#2 PR 2", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Pr_pane_renders_files_checks_reviews_and_the_merge_card()
    {
        var registry = Fixtures.Registry(Fixtures.ClientWith(1), null, new FakeProviderUi());
        var pane = (GitPrPaneViewModel)registry.Create(Fixtures.Descriptor("git.pr", """{"provider":"github","repo":"acme/widgets","number":1}"""));
        await pane.Detail.LoadAsync(TestContext.Current.CancellationToken);
        pane.Detail.SelectFile("src/a.cs");

        var view = registry.CreateView(pane);
        var texts = Texts(view);

        Assert.Contains(texts, t => t.StartsWith("src/a.cs", StringComparison.Ordinal));
        Assert.Contains("ci: check:success", texts);
        Assert.Contains("rev: review:approve", texts);
        Assert.Contains("Body text", texts);
        Assert.Contains("TWO", texts);
        var merge = view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Request owner merge"));
        Assert.True(merge.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Pr_pane_disables_merge_for_a_draft_and_says_why()
    {
        var client = new InMemoryGitGuiClient();
        client.Set(Fixtures.Detail(Fixtures.Summary(1, draft: true)), Fixtures.Files, []);
        var registry = Fixtures.Registry(client);
        var pane = (GitPrPaneViewModel)registry.Create(Fixtures.Descriptor("git.pr", """{"provider":"github","repo":"acme/widgets","number":1}"""));
        await pane.Detail.LoadAsync(TestContext.Current.CancellationToken);

        var view = registry.CreateView(pane);
        var texts = Texts(view);

        Assert.Contains("The PR is a draft.", texts);
        Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Request owner merge")).IsEnabled);
    }

    [AvaloniaFact]
    public async Task Pr_pane_shows_the_stale_selection_notice_after_head_moved()
    {
        var client = Fixtures.ClientWith(1);
        var registry = Fixtures.Registry(client);
        var pane = (GitPrPaneViewModel)registry.Create(Fixtures.Descriptor("git.pr", """{"provider":"github","repo":"acme/widgets","number":1}"""));
        await pane.Detail.LoadAsync(TestContext.Current.CancellationToken);
        pane.Detail.SelectFile("src/a.cs");
        client.MoveHead("github", "acme/widgets", 1, Fixtures.Head2);
        client.Replace("github", "acme/widgets", 1, Fixtures.Files, []);
        await pane.Detail.LoadAsync(TestContext.Current.CancellationToken);

        var texts = Texts(registry.CreateView(pane));

        Assert.Contains(texts, t => t.Contains("selection of src/a.cs was cleared", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Diff_pane_renders_the_named_file()
    {
        var registry = Fixtures.Registry(Fixtures.ClientWith(1));
        var pane = (GitDiffPaneViewModel)registry.Create(
            Fixtures.Descriptor("git.diff", """{"provider":"github","repo":"acme/widgets","number":1,"path":"src/b.cs"}"""));
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        var texts = Texts(registry.CreateView(pane));

        Assert.Contains("only", texts);
    }

    [AvaloniaFact]
    public async Task Diff_pane_without_a_path_shows_the_first_file()
    {
        var registry = Fixtures.Registry(Fixtures.ClientWith(1));
        var pane = (GitDiffPaneViewModel)registry.Create(Fixtures.Descriptor("git.diff", """{"provider":"github","repo":"acme/widgets","number":1}"""));
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("src/a.cs", pane.Detail.SelectedFile!.Path);
    }

    [AvaloniaFact]
    public async Task View_pane_renders_repos_branches_commits_and_constellation_buttons()
    {
        var registry = Fixtures.Registry(new InMemoryGitGuiClient(), new FakeRepoSource());
        var pane = (GitViewPaneViewModel)registry.Create(Fixtures.Descriptor("git.view", "{}"));
        await pane.View!.LoadAsync(TestContext.Current.CancellationToken);

        var view = registry.CreateView(pane);
        var texts = Texts(view);

        Assert.Contains("acme/widgets", view.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList());
        Assert.Contains(texts, t => t.Contains("first commit", StringComparison.Ordinal));
        Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Open in Constellation"));
    }

    [AvaloniaFact]
    public void View_pane_without_a_repo_source_says_so()
    {
        var registry = Fixtures.Registry(new InMemoryGitGuiClient());
        var pane = registry.Create(Fixtures.Descriptor("git.view", "{}"));

        Assert.Contains("No repository source is connected.", Texts(registry.CreateView(pane)));
    }

    [AvaloniaFact]
    public void A_bad_model_falls_back_to_the_generic_pane()
    {
        var registry = Fixtures.Registry(new InMemoryGitGuiClient());

        Assert.IsType<GenericPaneViewModel>(registry.Create(Fixtures.Descriptor("git.pr", """{"provider":"github"}""")));
        Assert.IsType<GenericPaneViewModel>(registry.Create(Fixtures.Descriptor("git.prs", "[]")));
    }

    [AvaloniaFact]
    public void Pane_without_a_client_shows_the_not_connected_error()
    {
        var registry = PaneRegistry.Compose(new PlainViews(), GitGuiPanes.Modules, new PaneModuleContext());
        var pane = registry.Create(Fixtures.Descriptor("git.prs", """{"provider":"github","repo":"acme/widgets"}"""));

        Assert.Contains("Git is not connected to a server.", Texts(registry.CreateView(pane)));
    }
}
