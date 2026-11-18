using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Athanor.Tests.Pane;

public sealed class AthanorPaneTests
{
    private static readonly EditorDocumentId _a = new("ws", "src/a.cs");
    private static readonly EditorDocumentId _b = new("ws", "src/b.cs");

    private sealed class MapSource : IEditorFileSource
    {
        private readonly Dictionary<EditorDocumentId, EditorDocument> _docs = new()
        {
            [_a] = new EditorDocument(_a, "alpha", "r1", "csharp", ReadOnly: false),
            [_b] = new EditorDocument(_b, "beta", "r1", "csharp", ReadOnly: false),
        };

        public Queue<SaveOutcome> Outcomes { get; } = new();

        public List<(string Content, string BaseRevision)> Saves { get; } = [];

        public Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken) =>
            Task.FromResult(_docs.GetValueOrDefault(id));

        public Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken)
        {
            Saves.Add((content, baseRevision));
            return Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : new SaveOutcome.Saved("r2"));
        }
    }

    private static (PaneRegistry Registry, AthanorPaneViewModel Pane, PlainTextFallbackHost Host, MapSource Source) Build()
    {
        var host = new PlainTextFallbackHost();
        var source = new MapSource();
        var context = new PaneModuleContext()
            .With<IEditorHost>(host)
            .With<IEditorFileSource>(source)
            .With<IEditorDraftStore>(new InMemoryEditorDraftStore());
        var registry = PaneRegistry.Compose([new AthanorPaneModule()], context);
        var pane = Assert.IsType<AthanorPaneViewModel>(registry.Create(Descriptor()));
        return (registry, pane, host, source);
    }

    private static PaneDescriptor Descriptor() =>
        new(AthanorPaneKinds.Editor, "Athanor", string.Empty, default);

    [Fact]
    public void Manifest_validates()
    {
        var result = ModuleManifestValidator.Validate(new AthanorClientModule().Manifest);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Client_module_registers_everything_its_manifest_declares()
    {
        var module = new AthanorClientModule();
        var builder = new ClientModuleBuilder(module.Manifest);

        module.Register(builder);

        Assert.Equal([AthanorPaneKinds.Editor], builder.RegisteredPaneKinds);
        Assert.Equal(4, builder.RegisteredCommands.Count);
        Assert.Contains("athanor.save", builder.RegisteredCommands);
    }

    [Fact]
    public void Pane_module_registers_its_kind()
    {
        var registry = PaneRegistry.Compose([new AthanorPaneModule()], new PaneModuleContext());

        Assert.True(registry.Contains(AthanorPaneKinds.Editor));
    }

    [Fact]
    public async Task Without_services_the_pane_is_an_empty_state()
    {
        var registry = PaneRegistry.Compose([new AthanorPaneModule()], new PaneModuleContext());
        var pane = Assert.IsType<AthanorPaneViewModel>(registry.Create(Descriptor()));

        Assert.True(pane.IsEmpty);
        Assert.Empty(pane.Tabs);
        Assert.False(await pane.OpenAsync(_a, TestContext.Current.CancellationToken));
        Assert.Null(await pane.SaveActiveAsync(TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public void Empty_state_view_shows_the_message()
    {
        var registry = PaneRegistry.Compose([new AthanorPaneModule()], new PaneModuleContext());
        var pane = Assert.IsType<AthanorPaneViewModel>(registry.Create(Descriptor()));

        var window = new Window { Content = registry.CreateView(pane) };
        window.Show();

        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == AthanorPaneViewModel.EmptyMessage);
    }

    [AvaloniaFact]
    public async Task Opening_editing_and_saving_updates_tabs_dirty_flag_and_active_tab()
    {
        var (registry, pane, host, source) = Build();
        var window = new Window { Content = registry.CreateView(pane) };
        window.Show();

        Assert.True(await pane.OpenAsync(_a, TestContext.Current.CancellationToken));
        Assert.True(await pane.OpenAsync(_b, TestContext.Current.CancellationToken));
        Assert.Equal(2, pane.Tabs.Count);
        Assert.Equal("b.cs", pane.ActiveTab?.Title);

        pane.Activate(_a);
        Assert.Equal("a.cs", pane.ActiveTab?.Title);
        var textBox = window.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal("alpha", textBox.Text);

        textBox.Text = "alpha edited";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("alpha edited", host.GetContent(_a));
        Assert.True(pane.ActiveTab?.Dirty);
        Assert.Equal("a.cs ●", pane.ActiveTab?.Label);
        Assert.False(pane.Tabs.Single(t => t.Id == _b).Dirty);

        var outcome = await pane.SaveActiveAsync(TestContext.Current.CancellationToken);

        Assert.IsType<SaveOutcome.Saved>(outcome);
        Assert.Equal(("alpha edited", "r1"), source.Saves.Single());
        Assert.False(pane.ActiveTab?.Dirty);
    }

    [AvaloniaFact]
    public async Task A_save_conflict_is_exposed_and_keep_mine_resolves_it()
    {
        var (registry, pane, host, source) = Build();
        var window = new Window { Content = registry.CreateView(pane) };
        window.Show();
        await pane.OpenAsync(_a, TestContext.Current.CancellationToken);
        host.Edit(_a, "mine");
        source.Outcomes.Enqueue(new SaveOutcome.Conflict("r5", "theirs"));

        await pane.SaveActiveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("r5", pane.ActiveConflict?.ServerRevision);
        Assert.True(pane.ActiveTab?.Dirty);

        await pane.KeepMineAsync();
        await pane.SaveActiveAsync(TestContext.Current.CancellationToken);

        Assert.Null(pane.ActiveConflict);
        Assert.Equal(("mine", "r5"), source.Saves[^1]);
    }

    [AvaloniaFact]
    public async Task Closing_the_active_tab_activates_a_neighbour()
    {
        var (_, pane, _, _) = Build();
        await pane.OpenAsync(_a, TestContext.Current.CancellationToken);
        await pane.OpenAsync(_b, TestContext.Current.CancellationToken);

        await pane.CloseAsync(_b, TestContext.Current.CancellationToken);

        Assert.Single(pane.Tabs);
        Assert.Equal("a.cs", pane.ActiveTab?.Title);
    }
}
