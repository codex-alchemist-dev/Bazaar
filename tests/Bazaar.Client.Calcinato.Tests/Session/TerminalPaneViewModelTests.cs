#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using System.ComponentModel;
using System.Text;
using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Calcinato.Tests.Sources;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Calcinato.Tests.Session;

public sealed class TerminalPaneViewModelTests
{
    private static async Task TypeAsync(TerminalSessionManager manager, string text)
    {
        foreach (char c in text)
        {
            await manager.HandleKeyAsync(TerminalKey.Typed(c), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Without_a_manager_the_pane_shows_its_empty_state()
    {
        using var pane = new TerminalPaneViewModel("Terminal", manager: null);

        Assert.Equal(PaneKinds.Terminal, pane.Kind);
        Assert.Equal("Terminal", pane.Title);
        Assert.False(pane.Snapshot.HasSession);
        Assert.Empty(pane.Snapshot.Rows);
        Assert.Equal(TerminalPaneSnapshot.NoSessionStatus, pane.Snapshot.StatusLine);
        Assert.Empty(pane.Accessible.VisibleRows);
    }

    [Fact]
    public async Task Snapshot_holds_styled_runs_cursor_and_mode()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        using var pane = new TerminalPaneViewModel("Terminal", manager);
        manager.OpenLocal();
        FakeTerminalSource source = host.LocalSources[0];

        source.Emit(Encoding.UTF8.GetBytes("ab\u001b[31mC"));

        await Eventually.TrueAsync(() => pane.Snapshot.Rows.Count > 0 && pane.Snapshot.Rows[0].Text == "abC");
        TerminalPaneSnapshot snapshot = pane.Snapshot;
        Assert.True(snapshot.HasSession);
        Assert.Equal(24, snapshot.Rows.Count);
        Assert.Equal("ab", snapshot.Rows[0].Runs[0].Text);
        Assert.Equal("C", snapshot.Rows[0].Runs[1].Text);
        Assert.NotEqual(snapshot.Rows[0].Runs[0].Style, snapshot.Rows[0].Runs[1].Style);
        Assert.Equal(0, snapshot.LiveCursorRow);
        Assert.Equal(3, snapshot.LiveCursorColumn);
        Assert.Equal("NORMAL", snapshot.ModeName);
        Assert.False(snapshot.IsReadOnly);
        Assert.False(snapshot.IsReplaying);
    }

    [Fact]
    public async Task Mode_and_command_line_follow_the_engine()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        using var pane = new TerminalPaneViewModel("Terminal", manager);
        manager.OpenLocal();

        await TypeAsync(manager, "i");
        Assert.Equal("INSERT", pane.Snapshot.ModeName);
        Assert.Equal(pane.Snapshot.LiveCursorRow, pane.Snapshot.CursorRow);

        await manager.HandleKeyAsync(TerminalKey.Named(KeyName.Escape), TestContext.Current.CancellationToken);
        await TypeAsync(manager, ":sp");
        Assert.Equal("COMMAND", pane.Snapshot.ModeName);
        Assert.Equal(":sp", pane.Snapshot.CommandLine);
    }

    [Fact]
    public async Task Read_only_replay_exit_and_title_indicators_are_reported()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        using var pane = new TerminalPaneViewModel("Terminal", manager);
        manager.OpenLocal();
        FakeTerminalSource source = host.LocalSources[0];

        source.Emit(Encoding.UTF8.GetBytes("\u001b]0;build\u0007old"), replay: true);
        await Eventually.TrueAsync(() => pane.Snapshot.IsReplaying && pane.Snapshot.Title == "build");

        source.Finish(3);
        await Eventually.TrueAsync(() => pane.Snapshot.ExitCode == 3);
        Assert.Equal("Exited with code 3.", pane.Snapshot.StatusLine);
    }

    [Fact]
    public async Task A_viewer_without_the_writer_role_is_flagged_read_only()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        using var pane = new TerminalPaneViewModel("Terminal", manager);
        host.RemoteCanWrite = false;
        await manager.AttachAsync("t5", TestContext.Current.CancellationToken);

        Assert.True(pane.Snapshot.HasSession);
        Assert.True(pane.Snapshot.IsReadOnly);
        Assert.Null(pane.Snapshot.ExitCode);
    }

    [Fact]
    public async Task Accessible_text_updates_are_rate_limited_and_the_last_state_is_published()
    {
        var clock = new FakeClock();
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host, clock);
        using var pane = new TerminalPaneViewModel("Terminal", manager, clock);
        int publishes = 0;
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalPaneViewModel.Accessible))
            {
                publishes++;
            }
        };

        manager.OpenLocal();
        Assert.Equal(1, clock.PendingTimers);
        Assert.Equal(0, publishes);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, publishes);

        FakeTerminalSource source = host.LocalSources[0];
        source.Emit("a");
        await Eventually.TrueAsync(() => clock.PendingTimers == 1 && pane.Snapshot.Rows[0].Text == "a");
        Assert.Equal(string.Empty, pane.Accessible.VisibleRows[0]);

        source.Emit("b");
        source.Emit("c");
        await Eventually.TrueAsync(() => pane.Snapshot.Rows[0].Text == "abc");
        Assert.Equal(1, clock.PendingTimers);
        Assert.Equal(1, publishes);

        clock.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal(2, publishes);
        Assert.Equal("abc", pane.Accessible.VisibleRows[0]);
        Assert.Equal(0, pane.Accessible.CursorRow);
        Assert.Equal("abc", pane.Accessible.CursorRowText);
    }

    [Fact]
    public async Task Accessible_text_is_published_at_once_when_the_interval_has_passed()
    {
        var clock = new FakeClock();
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host, clock);
        using var pane = new TerminalPaneViewModel("Terminal", manager, clock);
        int publishes = 0;
        pane.PropertyChanged += (_, e) => publishes += e.PropertyName == nameof(TerminalPaneViewModel.Accessible) ? 1 : 0;

        clock.Advance(TimeSpan.FromSeconds(1));
        manager.OpenLocal();

        Assert.Equal(1, publishes);
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task Disposing_the_pane_stops_it_following_the_manager()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        var pane = new TerminalPaneViewModel("Terminal", manager);
        pane.Dispose();

        manager.OpenLocal();

        Assert.False(pane.Snapshot.HasSession);
    }

    [Fact]
    public async Task Calcinato_modules_register_the_terminal_pane_kind()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        var context = new PaneModuleContext().With(manager);

        PaneRegistry registry = PaneRegistry.Compose(new UnusedPaneViewFactory(), CalcinatoPanes.Modules, context);

        Assert.True(registry.Contains(PaneKinds.Terminal));
        Assert.Contains(PaneKinds.Terminal, registry.Kinds);
        var pane = Assert.IsType<TerminalPaneViewModel>(registry.Create(new PaneDescriptor(PaneKinds.Terminal, "Shell", string.Empty, default)));
        Assert.Equal("Shell", pane.Title);
        pane.Dispose();
    }

    [Fact]
    public void The_module_still_registers_without_a_manager()
    {
        PaneRegistry registry = PaneRegistry.Compose(new UnusedPaneViewFactory(), CalcinatoPanes.Modules);

        var pane = Assert.IsType<TerminalPaneViewModel>(registry.Create(new PaneDescriptor(PaneKinds.Terminal, string.Empty, string.Empty, default)));
        Assert.Equal("Terminal", pane.Title);
        Assert.False(pane.Snapshot.HasSession);
        pane.Dispose();
    }
}
