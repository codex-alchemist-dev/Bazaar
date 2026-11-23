namespace Bazaar.Client.Windowing.Tests;

public sealed class WindowManagerTests
{
    [Fact]
    public async Task Opens_windows_with_cascaded_bounds_and_raises_Changed()
    {
        using var m = Make(out var host, out _);
        var changed = 0;
        m.Changed += (_, _) => changed++;

        var a = await m.Open(new WindowSpec(WindowKind.Editor, "a.txt", @"C:\a.txt"));
        var b = await m.Open(new WindowSpec(WindowKind.Terminal, "Terminal"));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a.Bounds.X, b.Bounds.X);
        Assert.Equal(2, host.Handles.Count);
        Assert.Equal(2, changed);
        Assert.Equal(2, m.Windows.Count);
    }

    [Fact]
    public async Task A_second_main_window_is_refused()
    {
        using var m = Make(out _, out _);
        Assert.NotNull(await m.Open(new WindowSpec(WindowKind.Main, "Bazaar")));
        Assert.Null(await m.Open(new WindowSpec(WindowKind.Main, "Bazaar")));
    }

    [Fact]
    public async Task Window_count_is_capped()
    {
        using var m = Make(out _, out _);
        for (var i = 0; i < WindowManager.MaxWindows; i++)
        {
            Assert.NotNull(await m.Open(new WindowSpec(WindowKind.Editor, $"f{i}")));
        }

        Assert.Null(await m.Open(new WindowSpec(WindowKind.Editor, "one too many")));
    }

    [Fact]
    public async Task Offscreen_bounds_are_pulled_onto_a_monitor()
    {
        using var m = Make(out _, out _);
        var w = await m.Open(new WindowSpec(WindowKind.Editor, "x", Bounds: new WindowBounds(9000, 9000, 800, 600)));
        Assert.NotNull(w);
        Assert.InRange(w.Bounds.X, 0, 1920 - w.Bounds.Width);
        Assert.Equal("m1", w.MonitorId);
    }

    [Fact]
    public async Task Close_is_vetoed_by_the_guard_and_main_never_closes_here()
    {
        using var m = Make(out var host, out var guard);
        var main = await m.Open(new WindowSpec(WindowKind.Main, "Bazaar"));
        var term = await m.Open(new WindowSpec(WindowKind.Terminal, "Terminal"));

        guard.Allow = false;
        Assert.False(await m.Close(term!.Id));
        Assert.Equal(2, m.Windows.Count);

        guard.Allow = true;
        Assert.True(await m.Close(term.Id));
        Assert.False(await m.Close(main!.Id));
        Assert.Single(m.Windows);
        Assert.True(host.Handles[term.Id].Closed);
    }

    [Fact]
    public async Task Close_request_from_the_host_goes_through_the_guard()
    {
        using var m = Make(out var host, out var guard);
        var w = await m.Open(new WindowSpec(WindowKind.Editor, "a"));
        guard.Allow = false;
        host.Handles[w!.Id].RaiseCloseRequested();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(m.Windows);

        guard.Allow = true;
        host.Handles[w.Id].RaiseCloseRequested();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(m.Windows);
    }

    [Fact]
    public async Task Detach_moves_a_pane_into_its_own_window_and_Redock_puts_it_back()
    {
        using var m = Make(out _, out _);
        var layout = new DockTabs([new DockPane("gc", "p1"), new DockPane("kanban", "p2")], 0);
        var main = await m.Open(new WindowSpec(WindowKind.Main, "Bazaar", Layout: layout));

        var detached = await m.Detach(main!.Id, "p2");

        Assert.NotNull(detached);
        Assert.Equal(WindowKind.Detached, detached.Kind);
        Assert.Equal(["p2"], DockOps.Panes(detached.Layout).Select(p => p.PaneId));
        var mainNow = m.Windows.Single(w => w.Id == main.Id);
        Assert.Equal(["p1"], DockOps.Panes(mainNow.Layout).Select(p => p.PaneId));

        Assert.True(await m.Redock(detached.Id, main.Id, "p1", DockEdge.Right));
        Assert.Single(m.Windows);
        Assert.Equal(["p1", "p2"], DockOps.Panes(m.Windows[0].Layout).Select(p => p.PaneId).Order());
    }

    [Fact]
    public async Task Detach_of_an_unknown_pane_or_window_does_nothing()
    {
        using var m = Make(out _, out _);
        var main = await m.Open(new WindowSpec(WindowKind.Main, "Bazaar", Layout: new DockPane("gc", "p1")));
        Assert.Null(await m.Detach(main!.Id, "nope"));
        Assert.Null(await m.Detach(new WindowId("ghost"), "p1"));
        Assert.Single(m.Windows);
    }

    [Fact]
    public async Task Redock_refuses_bad_targets()
    {
        using var m = Make(out _, out _);
        var main = await m.Open(new WindowSpec(WindowKind.Main, "Bazaar", Layout: new DockTabs([new DockPane("gc", "p1"), new DockPane("k", "p2")], 0)));
        var d = await m.Detach(main!.Id, "p2");
        Assert.False(await m.Redock(d!.Id, main.Id, "missing", DockEdge.Center));
        Assert.False(await m.Redock(main.Id, d.Id, "p2", DockEdge.Center));
        Assert.False(await m.Redock(d.Id, d.Id, "p2", DockEdge.Center));
        Assert.Equal(2, m.Windows.Count);
    }

    [Fact]
    public async Task Bounds_changes_from_the_host_update_the_record()
    {
        using var m = Make(out var host, out _);
        var w = await m.Open(new WindowSpec(WindowKind.Editor, "a"));
        host.Handles[w!.Id].RaiseBounds(new WindowBounds(10, 20, 500, 400));
        Assert.Equal(new WindowBounds(10, 20, 500, 400), m.Windows[0].Bounds);
    }

    [Fact]
    public async Task Activate_unknown_window_is_false()
    {
        using var m = Make(out _, out _);
        Assert.False(await m.Activate(new WindowId("ghost")));
    }

    [Fact]
    public async Task Concurrent_opens_get_unique_ids()
    {
        using var m = Make(out _, out _);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => m.Open(new WindowSpec(WindowKind.Editor, $"f{i}"))));
        Assert.Equal(20, results.Select(r => r!.Id).Distinct().Count());
    }

    private static WindowManager Make(out FakeWindowHost host, out FakeGuard guard)
    {
        host = new FakeWindowHost();
        guard = new FakeGuard();
        return new WindowManager(host, guard, new FakeMonitorSource(new MonitorInfo("m1", new WindowBounds(0, 0, 1920, 1080), 1.0, true)));
    }

    private sealed class FakeGuard : IWindowCloseGuard
    {
        public bool Allow { get; set; } = true;

        public ValueTask<bool> ConfirmCloseAsync(WindowRecord window, CancellationToken ct) => ValueTask.FromResult(Allow);
    }

    private sealed class FakeWindowHost : IWindowHost
    {
        public Dictionary<WindowId, FakeHandle> Handles { get; } = [];

        public ValueTask<IWindowHandle> OpenAsync(WindowRecord record, CancellationToken ct)
        {
            var h = new FakeHandle(record.Id);
            Handles[record.Id] = h;
            return ValueTask.FromResult<IWindowHandle>(h);
        }
    }

    private sealed class FakeHandle(WindowId id) : IWindowHandle
    {
        public WindowId Id { get; } = id;

        public bool Closed { get; private set; }

        public event EventHandler<WindowBounds>? BoundsChanged;

        public event EventHandler? CloseRequested;

        public void RaiseBounds(WindowBounds b) => BoundsChanged?.Invoke(this, b);

        public void RaiseCloseRequested() => CloseRequested?.Invoke(this, EventArgs.Empty);

        public ValueTask ActivateAsync() => ValueTask.CompletedTask;

        public ValueTask SetBoundsAsync(WindowBounds bounds) => ValueTask.CompletedTask;

        public ValueTask CloseAsync()
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal static class WindowManagerTestExtensions
{
    public static Task<WindowRecord?> Open(this WindowManager m, WindowSpec spec) => m.OpenAsync(spec, TestContext.Current.CancellationToken);

    public static Task<WindowRecord?> Detach(this WindowManager m, WindowId id, string paneId) => m.DetachAsync(id, paneId, TestContext.Current.CancellationToken);

    public static Task<bool> Redock(this WindowManager m, WindowId d, WindowId t, string pane, DockEdge edge) => m.RedockAsync(d, t, pane, edge, TestContext.Current.CancellationToken);

    public static Task<bool> Close(this WindowManager m, WindowId id) => m.CloseAsync(id, TestContext.Current.CancellationToken);

    public static Task<bool> Activate(this WindowManager m, WindowId id) => m.ActivateAsync(id, TestContext.Current.CancellationToken);
}
