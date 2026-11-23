using Bazaar.Client.Athanor.Workspace;

namespace Bazaar.Client.Athanor.Tests.Workspace;

public sealed class EditorWorkspaceTests
{
    private const string _workspaceId = "vault";

    private static EditorDocumentId Id(string path) => new(_workspaceId, path);

    private static EditorDocument Doc(string path, string content = "text", bool readOnly = false) =>
        new(Id(path), content, "rev-1", "csharp", readOnly);

    private static (EditorWorkspace Workspace, FakeEditorHost Host, FakeEditorFileSource Source) Create(params string[] paths)
    {
        var host = new FakeEditorHost();
        var source = new FakeEditorFileSource();
        foreach (string path in paths)
        {
            source.Add(Doc(path));
        }

        return (new EditorWorkspace(host, source), host, source);
    }

    private static async Task OpenAll(EditorWorkspace workspace, params string[] paths)
    {
        foreach (string path in paths)
        {
            Assert.True(await workspace.OpenAsync(Id(path), TestContext.Current.CancellationToken));
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task OpenAsync_AddsActiveTabWithTitleLanguageAndFlags()
    {
        var (workspace, host, _) = Create("notes/todo.md");

        bool opened = await workspace.OpenAsync(Id("notes/todo.md"), TestContext.Current.CancellationToken);

        Assert.True(opened);
        EditorTab tab = Assert.Single(workspace.Tabs);
        Assert.Equal(Id("notes/todo.md"), tab.Id);
        Assert.Equal("todo.md", tab.Title);
        Assert.Equal("csharp", tab.LanguageId);
        Assert.False(tab.Dirty);
        Assert.False(tab.ReadOnly);
        Assert.Equal(tab, workspace.ActiveTab);
        Assert.Single(host.Opened);
    }

    [Fact]
    public async Task OpenAsync_TitleIsWholePathWhenThereIsNoSlash()
    {
        var (workspace, _, _) = Create("README");

        await OpenAll(workspace, "README");

        Assert.Equal("README", Assert.Single(workspace.Tabs).Title);
    }

    [Fact]
    public async Task OpenAsync_DuplicateOpenOnlyActivatesTheExistingTab()
    {
        var (workspace, host, source) = Create("a.cs", "b.cs");
        await OpenAll(workspace, "a.cs", "b.cs");

        bool opened = await workspace.OpenAsync(Id("a.cs"), TestContext.Current.CancellationToken);

        Assert.True(opened);
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Equal(Id("a.cs"), workspace.ActiveTab!.Id);
        Assert.Equal(2, host.Opened.Count);
        Assert.Equal(2, source.OpenCalls);
    }

    [Fact]
    public async Task OpenAsync_MissingDocumentReturnsFalseAndAddsNoTab()
    {
        var (workspace, host, _) = Create();

        bool opened = await workspace.OpenAsync(Id("ghost.cs"), TestContext.Current.CancellationToken);

        Assert.False(opened);
        Assert.Empty(workspace.Tabs);
        Assert.Null(workspace.ActiveTab);
        Assert.Empty(host.Opened);
    }

    [Fact]
    public async Task OpenAsync_BinaryContentIsRefused()
    {
        var host = new FakeEditorHost();
        var source = new FakeEditorFileSource();
        source.Add(Doc("image.cs", "PK\0\u0003\u0004"));
        var workspace = new EditorWorkspace(host, source);

        bool opened = await workspace.OpenAsync(Id("image.cs"), TestContext.Current.CancellationToken);

        Assert.False(opened);
        Assert.Empty(workspace.Tabs);
        Assert.Empty(host.Opened);
    }

    [Fact]
    public async Task OpenAsync_DocumentOverFiveMiBOpensReadOnly()
    {
        var host = new FakeEditorHost();
        var source = new FakeEditorFileSource();
        source.Add(Doc("big.log", new string('a', EditorWorkspace.MaxEditableBytes + 1)));
        var workspace = new EditorWorkspace(host, source);

        bool opened = await workspace.OpenAsync(Id("big.log"), TestContext.Current.CancellationToken);

        Assert.True(opened);
        Assert.True(Assert.Single(workspace.Tabs).ReadOnly);
        Assert.True(Assert.Single(host.Opened).ReadOnly);
    }

    [Fact]
    public async Task OpenAsync_DocumentExactlyAtTheLimitStaysEditable()
    {
        var host = new FakeEditorHost();
        var source = new FakeEditorFileSource();
        source.Add(Doc("edge.log", new string('a', EditorWorkspace.MaxEditableBytes)));
        var workspace = new EditorWorkspace(host, source);

        await workspace.OpenAsync(Id("edge.log"), TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(workspace.Tabs).ReadOnly);
    }

    [Fact]
    public async Task OpenAsync_KeepsReadOnlyWhenTheSourceSaysSo()
    {
        var host = new FakeEditorHost();
        var source = new FakeEditorFileSource();
        source.Add(Doc("locked.cs", readOnly: true));
        var workspace = new EditorWorkspace(host, source);

        await workspace.OpenAsync(Id("locked.cs"), TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(workspace.Tabs).ReadOnly);
    }

    [Fact]
    public async Task OpenAsync_RaisesTabsChangedOnlyWhenTheTabsChange()
    {
        var (workspace, _, _) = Create("a.cs");
        int raised = 0;
        workspace.TabsChanged += (_, _) => raised++;

        await OpenAll(workspace, "a.cs");
        Assert.Equal(1, raised);

        await workspace.OpenAsync(Id("a.cs"), TestContext.Current.CancellationToken);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task CloseAsync_ActiveTabPicksTheRightNeighbour()
    {
        var (workspace, host, _) = Create("a.cs", "b.cs", "c.cs");
        await OpenAll(workspace, "a.cs", "b.cs", "c.cs");
        workspace.Activate(Id("b.cs"));

        await workspace.CloseAsync(Id("b.cs"), TestContext.Current.CancellationToken);

        Assert.Equal(Id("c.cs"), workspace.ActiveTab!.Id);
        Assert.Equal([Id("a.cs"), Id("c.cs")], workspace.Tabs.Select(t => t.Id));
        Assert.Equal([Id("b.cs")], host.Closed);
    }

    [Fact]
    public async Task CloseAsync_ClosingTheLastTabPicksTheLeftNeighbour()
    {
        var (workspace, _, _) = Create("a.cs", "b.cs", "c.cs");
        await OpenAll(workspace, "a.cs", "b.cs", "c.cs");

        await workspace.CloseAsync(Id("c.cs"), TestContext.Current.CancellationToken);

        Assert.Equal(Id("b.cs"), workspace.ActiveTab!.Id);
    }

    [Fact]
    public async Task CloseAsync_ClosingAnInactiveTabKeepsTheActiveTab()
    {
        var (workspace, _, _) = Create("a.cs", "b.cs", "c.cs");
        await OpenAll(workspace, "a.cs", "b.cs", "c.cs");

        await workspace.CloseAsync(Id("a.cs"), TestContext.Current.CancellationToken);

        Assert.Equal(Id("c.cs"), workspace.ActiveTab!.Id);
        Assert.Equal([Id("b.cs"), Id("c.cs")], workspace.Tabs.Select(t => t.Id));
    }

    [Fact]
    public async Task CloseAsync_ClosingTheLastRemainingTabLeavesNoActiveTab()
    {
        var (workspace, _, _) = Create("a.cs");
        await OpenAll(workspace, "a.cs");

        await workspace.CloseAsync(Id("a.cs"), TestContext.Current.CancellationToken);

        Assert.Empty(workspace.Tabs);
        Assert.Null(workspace.ActiveTab);
    }

    [Fact]
    public async Task CloseAsync_UnknownDocumentDoesNothing()
    {
        var (workspace, host, _) = Create("a.cs");
        await OpenAll(workspace, "a.cs");

        await workspace.CloseAsync(Id("other.cs"), TestContext.Current.CancellationToken);

        Assert.Single(workspace.Tabs);
        Assert.Empty(host.Closed);
    }

    [Fact]
    public async Task MoveTab_ReordersTabsAndKeepsTheActiveTab()
    {
        var (workspace, _, _) = Create("a.cs", "b.cs", "c.cs");
        await OpenAll(workspace, "a.cs", "b.cs", "c.cs");
        workspace.Activate(Id("b.cs"));

        workspace.MoveTab(Id("a.cs"), 2);

        Assert.Equal([Id("b.cs"), Id("c.cs"), Id("a.cs")], workspace.Tabs.Select(t => t.Id));
        Assert.Equal(Id("b.cs"), workspace.ActiveTab!.Id);
    }

    [Fact]
    public async Task MoveTab_RejectsAnIndexOutsideTheList()
    {
        var (workspace, _, _) = Create("a.cs", "b.cs");
        await OpenAll(workspace, "a.cs", "b.cs");

        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.MoveTab(Id("a.cs"), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.MoveTab(Id("a.cs"), -1));
        Assert.Equal([Id("a.cs"), Id("b.cs")], workspace.Tabs.Select(t => t.Id));
    }

    [Fact]
    public async Task NextAndPreviousTab_WrapAround()
    {
        var (workspace, _, _) = Create("a.cs", "b.cs", "c.cs");
        await OpenAll(workspace, "a.cs", "b.cs", "c.cs");

        workspace.NextTab();
        Assert.Equal(Id("a.cs"), workspace.ActiveTab!.Id);

        workspace.PreviousTab();
        Assert.Equal(Id("c.cs"), workspace.ActiveTab!.Id);

        workspace.PreviousTab();
        Assert.Equal(Id("b.cs"), workspace.ActiveTab!.Id);

        workspace.NextTab();
        Assert.Equal(Id("c.cs"), workspace.ActiveTab!.Id);
    }

    [Fact]
    public void NextAndPreviousTab_DoNothingWithoutTabs()
    {
        var (workspace, _, _) = Create();

        workspace.NextTab();
        workspace.PreviousTab();

        Assert.Null(workspace.ActiveTab);
    }

    [Fact]
    public async Task Dirty_FollowsTheHostAndIgnoresStaleVersions()
    {
        var (workspace, host, _) = Create("a.cs", "b.cs");
        await OpenAll(workspace, "a.cs", "b.cs");

        host.RaiseChanged(new EditorChange(Id("a.cs"), 1, "edit", Dirty: true));
        Assert.True(workspace.Tabs.Single(t => t.Id == Id("a.cs")).Dirty);
        Assert.False(workspace.Tabs.Single(t => t.Id == Id("b.cs")).Dirty);

        host.RaiseChanged(new EditorChange(Id("a.cs"), 1, "stale", Dirty: false));
        Assert.True(workspace.Tabs.Single(t => t.Id == Id("a.cs")).Dirty);

        host.RaiseChanged(new EditorChange(Id("a.cs"), 2, "saved", Dirty: false));
        Assert.False(workspace.Tabs.Single(t => t.Id == Id("a.cs")).Dirty);
    }

    [Fact]
    public async Task Dirty_ChangeForAnUnopenedDocumentIsIgnored()
    {
        var (workspace, host, _) = Create("a.cs");
        await OpenAll(workspace, "a.cs");
        int raised = 0;
        workspace.TabsChanged += (_, _) => raised++;

        host.RaiseChanged(new EditorChange(Id("other.cs"), 1, "x", Dirty: true));

        Assert.Equal(0, raised);
        Assert.False(Assert.Single(workspace.Tabs).Dirty);
    }

    [Fact]
    public async Task Dirty_RaisesTabsChangedWhenTheFlagFlips()
    {
        var (workspace, host, _) = Create("a.cs");
        await OpenAll(workspace, "a.cs");
        int raised = 0;
        workspace.TabsChanged += (_, _) => raised++;

        host.RaiseChanged(new EditorChange(Id("a.cs"), 1, "x", Dirty: true));
        host.RaiseChanged(new EditorChange(Id("a.cs"), 2, "y", Dirty: true));

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Crash_RaisesRendererCrashedAndKeepsTheTabs()
    {
        var (workspace, host, _) = Create("a.cs");
        await OpenAll(workspace, "a.cs");
        EditorCrashReason? reason = null;
        workspace.RendererCrashed += (_, r) => reason = r;

        host.RaiseCrashed(EditorCrashReason.RendererExited);

        Assert.Equal(EditorCrashReason.RendererExited, reason);
        Assert.Equal(Id("a.cs"), Assert.Single(workspace.Tabs).Id);
    }

    [Fact]
    public async Task Ready_AfterCrashReopensEveryTabWithItsLastContent()
    {
        var (workspace, host, _) = Create("a.cs", "b.cs");
        await OpenAll(workspace, "a.cs", "b.cs");
        host.RaiseChanged(new EditorChange(Id("b.cs"), 1, "unsaved edit", Dirty: true));
        host.RaiseCrashed(EditorCrashReason.RendererExited);

        host.RaiseReady();

        await WaitForAsync(() => host.Opened.Count == 4);
        Assert.Equal([Id("a.cs"), Id("b.cs")], host.Opened.Skip(2).Select(d => d.Id));
        Assert.Equal("unsaved edit", host.Opened[3].Content);
        Assert.True(workspace.Tabs.Single(t => t.Id == Id("b.cs")).Dirty);
    }

    [Fact]
    public async Task Ready_ResetsVersionGuardSoANewRendererCanReportChanges()
    {
        var (workspace, host, _) = Create("a.cs");
        await OpenAll(workspace, "a.cs");
        host.RaiseChanged(new EditorChange(Id("a.cs"), 5, "x", Dirty: true));
        host.RaiseCrashed(EditorCrashReason.BridgeFault);
        host.RaiseReady();
        await WaitForAsync(() => host.Opened.Count == 2);

        host.RaiseChanged(new EditorChange(Id("a.cs"), 1, "y", Dirty: false));

        Assert.False(Assert.Single(workspace.Tabs).Dirty);
    }

    [Fact]
    public async Task Ready_AFailedReopenDoesNotFaultTheWorkspace()
    {
        var (workspace, host, _) = Create("a.cs");
        await OpenAll(workspace, "a.cs");
        host.FailOpens = true;

        host.RaiseReady();
        await WaitForAsync(() => host.OpenAttempts == 2);

        host.FailOpens = false;
        Assert.True(await workspace.OpenAsync(Id("a.cs"), TestContext.Current.CancellationToken));
        Assert.Equal(Id("a.cs"), Assert.Single(workspace.Tabs).Id);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromHostAndDisposesIt()
    {
        var (workspace, host, _) = Create("a.cs");
        await OpenAll(workspace, "a.cs");
        int raised = 0;
        workspace.TabsChanged += (_, _) => raised++;

        await workspace.DisposeAsync();

        Assert.True(host.Disposed);
        Assert.Equal(0, host.ChangedSubscribers);
        host.RaiseChanged(new EditorChange(Id("a.cs"), 1, "x", Dirty: true));
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndBlocksLaterUse()
    {
        var (workspace, _, _) = Create("a.cs");

        await workspace.DisposeAsync();
        await workspace.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => workspace.OpenAsync(Id("a.cs"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new EditorWorkspace(null!, new FakeEditorFileSource()));
        Assert.Throws<ArgumentNullException>(() => new EditorWorkspace(new FakeEditorHost(), null!));
    }
}
