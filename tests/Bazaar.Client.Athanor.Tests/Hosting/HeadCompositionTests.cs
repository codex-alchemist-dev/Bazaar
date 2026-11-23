using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;
using Bazaar.Client.Athanor.Tests.Workspace;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Athanor.Tests.Hosting;

public sealed class HeadCompositionTests
{
    private static EditorHostCandidate Candidate(EditorHostKind kind, bool available = true, IEditorHost? host = null, Exception? fail = null) =>
        new(kind, () => available, () => fail is null ? host ?? new FakeEditorHost() : throw fail);

    [Theory]
    [InlineData(null, EditorHostKind.Auto, true)]
    [InlineData("", EditorHostKind.Auto, true)]
    [InlineData("auto", EditorHostKind.Auto, true)]
    [InlineData(" WebView2 ", EditorHostKind.WebView2, true)]
    [InlineData("gtk", EditorHostKind.Gtk, true)]
    [InlineData("AvaloniaEdit", EditorHostKind.AvaloniaEdit, true)]
    [InlineData("plain", EditorHostKind.PlainText, true)]
    [InlineData("monaco", EditorHostKind.Auto, false)]
    public void Preference_parses(string? text, EditorHostKind expected, bool valid)
    {
        Assert.Equal(valid, EditorHostSelector.TryParse(text, out var kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Default_orders_follow_the_platform_and_always_end_with_plain_text()
    {
        Assert.Equal([EditorHostKind.WebView2, EditorHostKind.AvaloniaEdit, EditorHostKind.PlainText], EditorHostSelector.Order(true, EditorHostKind.Auto));
        Assert.Equal([EditorHostKind.AvaloniaEdit, EditorHostKind.PlainText], EditorHostSelector.Order(false, EditorHostKind.Auto));
        Assert.Equal([EditorHostKind.Gtk, EditorHostKind.AvaloniaEdit, EditorHostKind.PlainText], EditorHostSelector.Order(false, EditorHostKind.Gtk));
        Assert.Equal([EditorHostKind.AvaloniaEdit, EditorHostKind.WebView2, EditorHostKind.PlainText], EditorHostSelector.Order(true, EditorHostKind.AvaloniaEdit));
        Assert.Equal(EditorHostKind.PlainText, EditorHostSelector.Order(true, EditorHostKind.PlainText)[0]);
    }

    [Fact]
    public void First_available_candidate_wins()
    {
        var web = new FakeEditorHost();
        var choice = EditorHostSelector.Select(
            EditorHostSelector.Order(true, EditorHostKind.Auto),
            [Candidate(EditorHostKind.WebView2, host: web), Candidate(EditorHostKind.AvaloniaEdit)]);

        Assert.Same(web, choice.Host);
        Assert.Equal(EditorHostKind.WebView2, choice.Kind);
        Assert.Empty(choice.Skipped);
    }

    [Fact]
    public void Unavailable_or_failing_candidates_fall_back_in_order_and_say_why()
    {
        var edit = new FakeEditorHost();
        var choice = EditorHostSelector.Select(
            EditorHostSelector.Order(true, EditorHostKind.Auto),
            [Candidate(EditorHostKind.WebView2, available: false), Candidate(EditorHostKind.AvaloniaEdit, host: edit)]);
        Assert.Same(edit, choice.Host);
        Assert.Contains(choice.Skipped, s => s.StartsWith("WebView2", StringComparison.Ordinal));

        var failing = EditorHostSelector.Select(
            EditorHostSelector.Order(true, EditorHostKind.Auto),
            [Candidate(EditorHostKind.WebView2, fail: new InvalidOperationException("no runtime")), Candidate(EditorHostKind.AvaloniaEdit, fail: new InvalidOperationException("no theme"))]);
        Assert.Equal(EditorHostKind.PlainText, failing.Kind);
        Assert.IsType<PlainTextFallbackHost>(failing.Host);
        Assert.Equal(2, failing.Skipped.Count);
        Assert.Contains("no runtime", failing.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Availability_check_that_throws_is_a_skip()
    {
        var choice = EditorHostSelector.Select(
            [EditorHostKind.Gtk, EditorHostKind.PlainText],
            [new EditorHostCandidate(EditorHostKind.Gtk, () => throw new IOException("denied"), () => new FakeEditorHost())]);

        Assert.Equal(EditorHostKind.PlainText, choice.Kind);
        Assert.Contains("denied", Assert.Single(choice.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void Gtk_is_used_only_when_asked_for_and_offered()
    {
        var gtk = new FakeEditorHost();
        var offered = new[] { Candidate(EditorHostKind.Gtk, host: gtk), Candidate(EditorHostKind.AvaloniaEdit) };

        Assert.Same(gtk, EditorHostSelector.Select(EditorHostSelector.Order(false, EditorHostKind.Gtk), offered).Host);
        Assert.Equal(EditorHostKind.AvaloniaEdit, EditorHostSelector.Select(EditorHostSelector.Order(false, EditorHostKind.Auto), offered).Kind);

        var notOffered = EditorHostSelector.Select(EditorHostSelector.Order(true, EditorHostKind.Gtk), [Candidate(EditorHostKind.AvaloniaEdit)]);
        Assert.Equal(EditorHostKind.AvaloniaEdit, notOffered.Kind);
        Assert.Contains("not offered", notOffered.Skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Options_read_the_environment_with_safe_defaults()
    {
        var root = Path.GetFullPath("data-root");
        var defaults = AthanorHeadOptions.FromEnvironment(_ => null, root);
        Assert.Equal(EditorHostKind.Auto, defaults.Host);
        Assert.Equal(root, defaults.DataDirectory);
        Assert.Null(defaults.Warning);
        Assert.Null(defaults.GtkHelperPath);
        Assert.Equal(Path.Combine(root, "athanor-drafts"), defaults.DraftsDirectory);
        Assert.Equal(Path.Combine(root, "athanor-webview2"), defaults.WebView2UserDataFolder);

        var other = Path.GetFullPath("elsewhere");
        var set = AthanorHeadOptions.FromEnvironment(
            name => name switch
            {
                AthanorHeadOptions.HostVariable => "gtk",
                AthanorHeadOptions.DataVariable => other,
                AthanorHeadOptions.GtkHelperVariable => " /opt/helper ",
                _ => null,
            },
            root);
        Assert.Equal(EditorHostKind.Gtk, set.Host);
        Assert.Equal(other, set.DataDirectory);
        Assert.Equal("/opt/helper", set.GtkHelperPath);
    }

    [Fact]
    public void Bad_settings_fall_back_and_warn()
    {
        var root = Path.GetFullPath("data-root");
        var options = AthanorHeadOptions.FromEnvironment(
            name => name switch
            {
                AthanorHeadOptions.HostVariable => "monaco",
                AthanorHeadOptions.DataVariable => "relative/dir",
                _ => null,
            },
            root);

        Assert.Equal(EditorHostKind.Auto, options.Host);
        Assert.Equal(root, options.DataDirectory);
        Assert.Contains(AthanorHeadOptions.HostVariable, options.Warning, StringComparison.Ordinal);
        Assert.Contains(AthanorHeadOptions.DataVariable, options.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void File_source_is_wire_when_connected_and_offline_otherwise()
    {
        var wire = new OfflineEditorFileSource();
        Assert.Same(wire, EditorFileSourceSelector.Select(() => wire));
        Assert.IsType<OfflineEditorFileSource>(EditorFileSourceSelector.Select(null));
        Assert.IsType<OfflineEditorFileSource>(EditorFileSourceSelector.Select(() => null));

        var report = new List<string>();
        Assert.IsType<OfflineEditorFileSource>(EditorFileSourceSelector.Select(() => throw new InvalidOperationException("bad url"), report.Add));
        Assert.Contains("bad url", Assert.Single(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Router_table_from_contributions_builds_and_conflicts_claim_nothing()
    {
        var commands = AthanorClientModule.Commands;
        Assert.NotNull(EditorRouterTable.Create(commands));
        Assert.Null(EditorRouterTable.Create([.. commands, new ClientCommandContribution("x.dup", "dup", "edsave")]));
    }

    [AvaloniaFact]
    public async Task Pane_places_a_surface_host_in_the_layout_instead_of_the_text_box()
    {
        var surface = new Border();
        var host = new SurfaceHost(surface);
        var context = new PaneModuleContext()
            .With<IEditorHost>(host)
            .With<IEditorFileSource>(new OfflineEditorFileSource())
            .With<IEditorDraftStore>(new InMemoryEditorDraftStore());
        var registry = PaneRegistry.Compose(PaneViews.Instance, [new AthanorPaneModule()], context);
        var pane = Assert.IsType<AthanorPaneViewModel>(registry.Create(AthanorPaneModule.WindowPane));

        var window = new Window { Content = registry.CreateView(pane) };
        window.Show();

        Assert.Contains(window.GetVisualDescendants(), c => ReferenceEquals(c, surface));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsVisible);
        await pane.DisposeAsync();
    }

    private sealed class SurfaceHost(Control surface) : IEditorHost, IEditorSurfaceHost
    {
        private readonly FakeEditorHost _inner = new();

        public Control Surface { get; } = surface;

        public Task OpenAsync(EditorDocument document, CancellationToken cancellationToken) => _inner.OpenAsync(document, cancellationToken);

        public Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken) => _inner.CloseAsync(id, cancellationToken);

        public Task FocusAsync(CancellationToken cancellationToken) => _inner.FocusAsync(cancellationToken);

        public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken) => _inner.SetThemeAsync(monacoThemeJson, cancellationToken);

        public Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken) => _inner.SetReadOnlyAsync(id, isReadOnly, cancellationToken);

        public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken) => _inner.ShowDiffAsync(original, modified, languageId, cancellationToken);

        public event EventHandler<EditorChange>? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }

        public event EventHandler<EditorDocumentId>? SaveRequested
        {
            add => _inner.SaveRequested += value;
            remove => _inner.SaveRequested -= value;
        }

        public event EventHandler<EditorKeyEvent>? KeyPressed
        {
            add => _inner.KeyPressed += value;
            remove => _inner.KeyPressed -= value;
        }

        public event EventHandler? Ready
        {
            add => _inner.Ready += value;
            remove => _inner.Ready -= value;
        }

        public event EventHandler<EditorCrashReason>? Crashed
        {
            add => _inner.Crashed += value;
            remove => _inner.Crashed -= value;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
