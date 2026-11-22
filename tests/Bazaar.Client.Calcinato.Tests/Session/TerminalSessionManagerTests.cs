#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Calcinato.Tests.Sources;

namespace Bazaar.Client.Calcinato.Tests.Session;

public sealed class TerminalSessionManagerTests
{
    private static ExCommand Command(ExCommandKind kind, string? argument = null) => new(kind, argument);

    private static async Task TypeLineAsync(TerminalSessionManager manager, string line)
    {
        foreach (char c in line)
        {
            await manager.HandleKeyAsync(TerminalKey.Typed(c), TestContext.Current.CancellationToken);
        }

        await manager.HandleKeyAsync(TerminalKey.Named(KeyName.Enter), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Open_local_makes_the_new_session_active_and_lays_it_out()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);

        var session = manager.OpenLocal("/bin/sh");

        Assert.Same(session, manager.Active);
        Assert.Equal("local-1", session.Id);
        Assert.Equal(new[] { "local-1" }, manager.Layout.SessionIds);
        Assert.Equal(TerminalOrientation.Horizontal, manager.Layout.Orientation);
        var spec = Assert.Single(host.Opened);
        Assert.Equal("/bin/sh", spec.Shell);
        Assert.Equal((80, 24), (spec.Cols, spec.Rows));
    }

    [Fact]
    public async Task New_sessions_go_after_the_active_one_and_activate_keeps_order()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        manager.OpenLocal();
        manager.OpenLocal();
        manager.OpenLocal();

        Assert.True(manager.Activate("local-1"));
        manager.OpenLocal();

        Assert.Equal(new[] { "local-1", "local-4", "local-2", "local-3" }, manager.Layout.SessionIds);
        Assert.Equal("local-4", manager.Active?.Id);
    }

    [Fact]
    public async Task Activate_returns_false_for_an_unknown_session()
    {
        await using var manager = new TerminalSessionManager(new FakeTerminalHost(supportsLocal: true));
        manager.OpenLocal();

        Assert.False(manager.Activate("missing"));
        Assert.Equal("local-1", manager.Active?.Id);
    }

    [Fact]
    public async Task Split_and_vsplit_set_the_orientation_and_open_a_shell()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);

        await manager.ExecuteAsync(Command(ExCommandKind.Split), TestContext.Current.CancellationToken);
        Assert.Equal(TerminalOrientation.Horizontal, manager.Layout.Orientation);
        Assert.Single(manager.Layout.SessionIds);

        var result = await manager.ExecuteAsync(Command(ExCommandKind.VSplit), TestContext.Current.CancellationToken);
        Assert.True(result.Handled);
        Assert.Equal(TerminalOrientation.Vertical, manager.Layout.Orientation);
        Assert.Equal(2, manager.Layout.SessionIds.Count);
        Assert.Equal(2, host.Opened.Count);
    }

    [Fact]
    public async Task New_reports_when_the_head_has_no_local_backend()
    {
        var host = new FakeTerminalHost(supportsLocal: false);
        await using var manager = new TerminalSessionManager(host);

        var result = await manager.ExecuteAsync(Command(ExCommandKind.New), TestContext.Current.CancellationToken);

        Assert.True(result.Handled);
        Assert.Contains("no local terminal backend", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.Sessions);
        Assert.Empty(host.Opened);
    }

    [Fact]
    public async Task Attach_opens_a_remote_session_once()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);

        await manager.ExecuteAsync(Command(ExCommandKind.Attach, "t9"), TestContext.Current.CancellationToken);
        var second = await manager.ExecuteAsync(Command(ExCommandKind.Attach, "t9"), TestContext.Current.CancellationToken);

        Assert.Equal("remote:t9", manager.Active?.Id);
        Assert.True(manager.Active?.IsRemote);
        Assert.Equal(new[] { "t9" }, host.AttachedIds);
        Assert.Single(manager.Sessions);
        Assert.Contains("Already attached", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_without_a_terminal_id_shows_usage()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);

        var result = await manager.ExecuteAsync(Command(ExCommandKind.Attach), TestContext.Current.CancellationToken);

        Assert.Equal("Usage: :attach <terminalId>", result.Message);
        Assert.Empty(host.AttachedIds);
    }

    [Fact]
    public async Task Quitting_a_running_local_shell_asks_for_confirmation_first()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        manager.OpenLocal();
        TerminalCloseRequest? request = null;
        manager.CloseRequested += (_, r) => request = r;

        var result = await manager.ExecuteAsync(Command(ExCommandKind.Quit), TestContext.Current.CancellationToken);

        Assert.True(result.NeedsConfirmation);
        Assert.Equal(new TerminalCloseRequest("local-1", HasRunningProcess: true), request);
        Assert.Single(manager.Sessions);
        Assert.False(host.LocalSources[0].Disposed);
    }

    [Fact]
    public async Task Confirming_the_close_with_force_closes_and_ends_the_shell()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        manager.OpenLocal();

        var outcome = await manager.CloseAsync("local-1", force: true);

        Assert.Equal(TerminalCloseOutcome.Closed, outcome);
        Assert.Empty(manager.Sessions);
        Assert.Null(manager.Active);
        Assert.True(host.LocalSources[0].Disposed);
    }

    [Fact]
    public async Task Quitting_an_exited_shell_does_not_ask()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        var session = manager.OpenLocal();
        int requests = 0;
        manager.CloseRequested += (_, _) => requests++;
        host.LocalSources[0].Finish(0);
        await Eventually.TrueAsync(() => session.ExitCode == 0);

        var result = await manager.ExecuteAsync(Command(ExCommandKind.Quit), TestContext.Current.CancellationToken);

        Assert.False(result.NeedsConfirmation);
        Assert.Equal(0, requests);
        Assert.Empty(manager.Sessions);
    }

    [Fact]
    public async Task Quit_force_kills_a_running_shell_without_asking()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        manager.OpenLocal();
        int requests = 0;
        manager.CloseRequested += (_, _) => requests++;

        var result = await manager.ExecuteAsync(Command(ExCommandKind.QuitForce), TestContext.Current.CancellationToken);

        Assert.Equal("Closed local-1.", result.Message);
        Assert.Equal(0, requests);
        Assert.True(host.LocalSources[0].Disposed);
        Assert.Empty(manager.Sessions);
    }

    [Fact]
    public async Task Closing_the_active_session_moves_focus_to_its_neighbour()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        manager.OpenLocal();
        manager.OpenLocal();
        manager.OpenLocal();
        manager.Activate("local-2");

        await manager.CloseAsync("local-2", force: true);

        Assert.Equal(new[] { "local-1", "local-3" }, manager.Layout.SessionIds);
        Assert.Equal("local-3", manager.Active?.Id);
    }

    [Fact]
    public async Task Closing_an_unknown_session_reports_not_found()
    {
        await using var manager = new TerminalSessionManager(new FakeTerminalHost(supportsLocal: true));

        Assert.Equal(TerminalCloseOutcome.NotFound, await manager.CloseAsync("missing", force: false));
    }

    [Fact]
    public async Task Detach_closes_a_remote_viewer_and_keeps_the_server_terminal()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        await manager.AttachAsync("t1", TestContext.Current.CancellationToken);

        var result = await manager.ExecuteAsync(Command(ExCommandKind.Detach), TestContext.Current.CancellationToken);

        Assert.Contains("keeps running", result.Message, StringComparison.Ordinal);
        Assert.Empty(manager.Sessions);
        Assert.True(host.RemoteSources[0].Disposed);
    }

    [Fact]
    public async Task Detach_refuses_a_local_shell()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        manager.OpenLocal();

        var result = await manager.ExecuteAsync(Command(ExCommandKind.Detach), TestContext.Current.CancellationToken);

        Assert.Contains("cannot be detached", result.Message, StringComparison.Ordinal);
        Assert.Single(manager.Sessions);
        Assert.False(host.LocalSources[0].Disposed);
    }

    [Fact]
    public async Task Commands_the_manager_does_not_own_are_left_to_the_head()
    {
        await using var manager = new TerminalSessionManager(new FakeTerminalHost(supportsLocal: true));

        var result = await manager.ExecuteAsync(Command(ExCommandKind.Take), TestContext.Current.CancellationToken);

        Assert.False(result.Handled);
        Assert.Contains("Take", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ex_line_typed_into_the_active_session_is_routed_by_the_manager()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        manager.OpenLocal();

        await TypeLineAsync(manager, ":split");

        Assert.Equal(TerminalOrientation.Horizontal, manager.Layout.Orientation);
        Assert.Equal(2, manager.Layout.SessionIds.Count);
        Assert.Empty(host.LocalSources[0].Writes);
    }

    [Fact]
    public async Task Keys_with_no_session_open_report_how_to_start_one()
    {
        await using var manager = new TerminalSessionManager(new FakeTerminalHost(supportsLocal: true));

        await manager.HandleKeyAsync(TerminalKey.Typed('i'), TestContext.Current.CancellationToken);

        Assert.Equal("No terminal is open. Use :new to start a shell.", manager.Message);
    }

    [Fact]
    public async Task Changed_is_raised_for_structure_and_for_open_session_output()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        await using var manager = new TerminalSessionManager(host);
        int changes = 0;
        manager.Changed += (_, _) => Interlocked.Increment(ref changes);

        manager.OpenLocal();
        int afterOpen = Volatile.Read(ref changes);
        host.LocalSources[0].Emit("output");

        Assert.True(afterOpen > 0);
        await Eventually.TrueAsync(() => Volatile.Read(ref changes) > afterOpen);
    }

    [Fact]
    public async Task Dispose_closes_every_session_and_disposes_their_sources()
    {
        var host = new FakeTerminalHost(supportsLocal: true);
        var manager = new TerminalSessionManager(host);
        manager.OpenLocal();
        await manager.AttachAsync("t2", TestContext.Current.CancellationToken);

        await manager.DisposeAsync();

        Assert.Empty(manager.Sessions);
        Assert.Null(manager.Active);
        Assert.True(host.LocalSources[0].Disposed);
        Assert.True(host.RemoteSources[0].Disposed);
    }

    [Fact]
    public void Rejects_a_non_positive_default_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalSessionManager(new FakeTerminalHost(true), defaultCols: 0));
    }
}
