using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Input;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Athanor.Tests.Pane;

public sealed class AthanorCommandTests
{
    private static readonly EditorDocumentId _a = new("ws", "src/a.cs");
    private static readonly EditorDocumentId _missing = new("ws", "src/none.cs");

    private sealed class MapSource : IEditorFileSource
    {
        public string ServerContent { get; set; } = "alpha";

        public List<(string Content, string BaseRevision)> Saves { get; } = [];

        public Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken) =>
            Task.FromResult<EditorDocument?>(id == _a ? new EditorDocument(_a, ServerContent, "r1", "csharp", ReadOnly: false) : null);

        public Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken)
        {
            Saves.Add((content, baseRevision));
            return Task.FromResult<SaveOutcome>(new SaveOutcome.Saved("r2"));
        }
    }

    private sealed class Prompts(string? open, NewDocumentRequest? create) : IEditorCommandPrompts
    {
        public Task<string?> AskOpenPathAsync(CancellationToken cancellationToken) => Task.FromResult(open);

        public Task<NewDocumentRequest?> AskNewDocumentAsync(CancellationToken cancellationToken) => Task.FromResult(create);
    }

    private sealed class DiffHost : IEditorHost
    {
        public List<(string Original, string Modified, string Language)> Diffs { get; } = [];

        public Task OpenAsync(EditorDocument document, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FocusAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken)
        {
            Diffs.Add((original, modified, languageId));
            return Task.CompletedTask;
        }

        public event EventHandler<EditorChange>? Changed
        {
            add { }
            remove { }
        }

        public event EventHandler<EditorDocumentId>? SaveRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<EditorKeyEvent>? KeyPressed
        {
            add { }
            remove { }
        }

        public event EventHandler? Ready
        {
            add { }
            remove { }
        }

        public event EventHandler<EditorCrashReason>? Crashed
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (AthanorPaneViewModel Pane, PlainTextFallbackHost Host, MapSource Source) Build()
    {
        var host = new PlainTextFallbackHost();
        var source = new MapSource();
        return (new AthanorPaneViewModel("Athanor", host, source, new InMemoryEditorDraftStore()), host, source);
    }

    [Fact]
    public void Contributions_have_valid_non_ctrl_mnemonics_that_build_a_router_table()
    {
        var module = new AthanorClientModule();
        var builder = new ClientModuleBuilder(module.Manifest);
        module.Register(builder);

        Assert.Equal(4, builder.Contributions.Count);
        Assert.All(builder.Contributions, c => Assert.StartsWith("ed", c.Mnemonic, StringComparison.Ordinal));
        var table = MnemonicTable.Create(
            [KeyStroke.OfChar('p')],
            builder.Contributions.Select(c => new MnemonicBinding(c.Mnemonic, c.Id)));
        Assert.NotNull(table.Table);
        Assert.Equal("athanor.save", table.Table.Exact("edsave")?.CommandId);
        Assert.True(AthanorCommandHandler.Handles("athanor.diff"));
        Assert.False(AthanorCommandHandler.Handles("nav.gc.first"));
    }

    [Fact]
    public async Task Save_command_saves_the_active_document_and_reports_nothing_to_do_without_one()
    {
        var (pane, host, source) = Build();
        var handler = new AthanorCommandHandler(pane);
        var ct = TestContext.Current.CancellationToken;

        var none = await handler.InvokeAsync(AthanorClientModule.SaveCommandId, ct);
        Assert.Equal(AthanorCommandStatus.NothingToDo, none.Status);

        Assert.True(await pane.OpenAsync(_a, ct));
        host.Edit(_a, "alpha!");
        var saved = await handler.InvokeAsync(AthanorClientModule.SaveCommandId, ct);

        Assert.Equal(AthanorCommandStatus.Done, saved.Status);
        Assert.Equal([("alpha!", "r1")], source.Saves);
    }

    [Fact]
    public async Task Open_and_new_use_the_prompts_and_report_missing_documents()
    {
        var (pane, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        var noPrompts = await new AthanorCommandHandler(pane).InvokeAsync(AthanorClientModule.OpenCommandId, ct);
        Assert.Equal(AthanorCommandStatus.Unavailable, noPrompts.Status);

        var cancelled = await new AthanorCommandHandler(pane, new Prompts(null, null)).InvokeAsync(AthanorClientModule.OpenCommandId, ct);
        Assert.Equal(AthanorCommandStatus.NothingToDo, cancelled.Status);

        var handler = new AthanorCommandHandler(pane, new Prompts("ws:src/a.cs", new NewDocumentRequest("src/none.cs", "csharp")));
        Assert.Equal(AthanorCommandStatus.Done, (await handler.InvokeAsync(AthanorClientModule.OpenCommandId, ct)).Status);
        Assert.Single(pane.Tabs);
        var created = await handler.InvokeAsync(AthanorClientModule.NewCommandId, ct);
        Assert.Equal(AthanorCommandStatus.Failed, created.Status);
        Assert.Contains("none.cs", created.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_prompts_do_nothing_for_open_and_new()
    {
        var (pane, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;
        var handler = new AthanorCommandHandler(pane, new Prompts(null, null));

        Assert.Equal(AthanorCommandStatus.NothingToDo, (await handler.InvokeAsync(AthanorClientModule.OpenCommandId, ct)).Status);
        Assert.Equal(AthanorCommandStatus.NothingToDo, (await handler.InvokeAsync(AthanorClientModule.NewCommandId, ct)).Status);
        Assert.Empty(pane.Tabs);
    }

    [Fact]
    public async Task Invalid_paths_fail_without_opening_anything()
    {
        var (pane, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        foreach (var bad in new[] { "ws:../secret", "C:\\x", "plain/path.cs" })
        {
            var result = await new AthanorCommandHandler(pane, new Prompts(bad, null)).InvokeAsync(AthanorClientModule.OpenCommandId, ct);
            Assert.Equal(AthanorCommandStatus.Failed, result.Status);
        }

        Assert.Empty(pane.Tabs);
    }

    [Fact]
    public async Task A_bare_path_uses_the_active_documents_workspace_and_new_reports_the_language()
    {
        var (pane, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await pane.OpenAsync(_a, ct));

        var open = await new AthanorCommandHandler(pane, new Prompts("src\\a.cs", null)).InvokeAsync(AthanorClientModule.OpenCommandId, ct);
        Assert.Equal(AthanorCommandStatus.Done, open.Status);

        var created = await new AthanorCommandHandler(pane, new Prompts(null, new NewDocumentRequest("src/fresh.fs", "fsharp")))
            .InvokeAsync(AthanorClientModule.NewCommandId, ct);
        Assert.Equal(AthanorCommandStatus.Failed, created.Status);
        Assert.Contains("fsharp", created.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_creates_the_document_through_a_source_that_can_create_and_opens_it()
    {
        var pane = new AthanorPaneViewModel("Athanor", new PlainTextFallbackHost(), new InMemoryEditorFileSource(), new InMemoryEditorDraftStore());
        var ct = TestContext.Current.CancellationToken;
        var handler = new AthanorCommandHandler(pane, new Prompts(null, new NewDocumentRequest("ws:src/fresh.fs", "fsharp")));

        var first = await handler.InvokeAsync(AthanorClientModule.NewCommandId, ct);
        var again = await handler.InvokeAsync(AthanorClientModule.NewCommandId, ct);

        Assert.Equal(AthanorCommandStatus.Done, first.Status);
        Assert.Equal(AthanorCommandStatus.Done, again.Status);
        Assert.Single(pane.Tabs, t => t.Id == new EditorDocumentId("ws", "src/fresh.fs"));
    }

    [Fact]
    public async Task Prompts_can_be_supplied_after_the_handler_is_built()
    {
        var (pane, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;
        var handler = new AthanorCommandHandler(pane) { Prompts = new Prompts("ws:src/a.cs", null) };

        Assert.Equal(AthanorCommandStatus.Done, (await handler.InvokeAsync(AthanorClientModule.OpenCommandId, ct)).Status);
    }

    [Fact]
    public async Task Diff_command_shows_the_server_copy_against_the_current_text()
    {
        var host = new DiffHost();
        var source = new MapSource();
        var pane = new AthanorPaneViewModel("Athanor", host, source, new InMemoryEditorDraftStore());
        var handler = new AthanorCommandHandler(pane);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(AthanorCommandStatus.NothingToDo, (await handler.InvokeAsync(AthanorClientModule.DiffCommandId, ct)).Status);

        Assert.True(await pane.OpenAsync(_a, ct));
        source.ServerContent = "alpha server";
        var result = await handler.InvokeAsync(AthanorClientModule.DiffCommandId, ct);

        Assert.Equal(AthanorCommandStatus.Done, result.Status);
        var diff = Assert.Single(host.Diffs);
        Assert.Equal("alpha server", diff.Original);
        Assert.Equal("alpha", diff.Modified);
        Assert.Equal("csharp", diff.Language);
    }

    [Fact]
    public async Task Empty_pane_and_unknown_ids_are_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var empty = new AthanorCommandHandler(new AthanorPaneViewModel("Athanor"));
        Assert.Equal(AthanorCommandStatus.Unavailable, (await empty.InvokeAsync("athanor.save", ct)).Status);

        var (pane, _, _) = Build();
        Assert.Equal(AthanorCommandStatus.Unavailable, (await new AthanorCommandHandler(pane).InvokeAsync("athanor.zzz", ct)).Status);
    }

    [Fact]
    public void Pane_module_exposes_a_handler_after_registration()
    {
        var module = new AthanorPaneModule();
        Assert.Null(module.CommandHandler);
        _ = PaneRegistry.Compose(PaneViews.Instance, [module], new PaneModuleContext());
        Assert.NotNull(module.CommandHandler);
    }
}
