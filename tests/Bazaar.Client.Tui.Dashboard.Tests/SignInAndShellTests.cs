using Bazaar.Client.Tui.Tests;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard.Tests;

public class OpsSignInTests
{
    private const string _code = "ABCD-1234";

    private static (OpsSignIn SignIn, FakeSessions Sessions, RecordingAudit Audit, FakeClock Clock) Create(
        AdminIdentity? owner = null, bool authorized = true)
    {
        var sessions = new FakeSessions();
        var audit = new RecordingAudit();
        var clock = new FakeClock(Samples.Now);
        var other = new AdminIdentity(Guid.NewGuid(), Samples.Owner.Kind, Guid.NewGuid());
        return (new OpsSignIn(new FakeOwnerGate(owner), new FakeRedeemer(_code, other), sessions, new FakeAuthorizer(authorized), audit, clock), sessions, audit, clock);
    }

    [Fact]
    public async Task The_owner_on_the_box_gets_a_session_and_the_sign_in_is_audited()
    {
        var (signIn, sessions, audit, _) = Create(Samples.Owner);

        var session = await signIn.SignInOwnerAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal(OpsSignInMethod.OwnerLocal, session.Method);
        Assert.Equal(1, sessions.LiveCount);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AdminAuditKinds.SignIn, entry.Kind);
        Assert.Equal("owner-local", entry.Fields["method"]);
        Assert.Equal("tui", entry.Fields["surface"]);
    }

    [Fact]
    public async Task A_closed_owner_gate_refuses_and_audits_the_denial()
    {
        var (signIn, sessions, audit, _) = Create(owner: null);

        Assert.Null(await signIn.SignInOwnerAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, sessions.LiveCount);
        Assert.Equal(AdminAuditKinds.Denied, Assert.Single(audit.Entries).Kind);
    }

    [Fact]
    public async Task A_code_works_once_and_is_never_written_to_the_audit()
    {
        var (signIn, _, audit, _) = Create();

        var first = await signIn.SignInWithCodeAsync("  " + _code + " ", TestContext.Current.CancellationToken);
        var second = await signIn.SignInWithCodeAsync(_code, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(OpsSignInMethod.AdminCode, first.Method);
        Assert.Null(second);
        Assert.Equal([AdminAuditKinds.SignIn, AdminAuditKinds.Denied], audit.Entries.Select(e => e.Kind));
        Assert.All(audit.Entries, e =>
        {
            Assert.DoesNotContain(_code, e.Actor);
            Assert.All(e.Fields.Values, v => Assert.DoesNotContain(_code, v));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WRONG")]
    public async Task Wrong_or_empty_codes_are_refused_and_audited(string code)
    {
        var (signIn, sessions, audit, _) = Create();

        Assert.Null(await signIn.SignInWithCodeAsync(code, TestContext.Current.CancellationToken));
        Assert.Equal(0, sessions.LiveCount);
        Assert.Equal(AdminAuditKinds.Denied, Assert.Single(audit.Entries).Kind);
    }

    [Fact]
    public async Task A_valid_code_without_ops_rights_is_refused()
    {
        var (signIn, sessions, audit, _) = Create(authorized: false);

        Assert.Null(await signIn.SignInWithCodeAsync(_code, TestContext.Current.CancellationToken));
        Assert.Equal(0, sessions.LiveCount);
        Assert.Equal(AdminAuditKinds.Denied, Assert.Single(audit.Entries).Kind);
    }

    [Fact]
    public async Task A_revoked_session_stops_being_active_at_once()
    {
        var (signIn, sessions, _, _) = Create(Samples.Owner);
        var session = (await signIn.SignInOwnerAsync(TestContext.Current.CancellationToken))!;

        Assert.True(await signIn.IsActiveAsync(session, TestContext.Current.CancellationToken));
        sessions.RevokeAll();
        Assert.False(await signIn.IsActiveAsync(session, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Signing_out_revokes_and_audits()
    {
        var (signIn, sessions, audit, _) = Create(Samples.Owner);
        var session = (await signIn.SignInOwnerAsync(TestContext.Current.CancellationToken))!;

        await signIn.SignOutAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(0, sessions.LiveCount);
        Assert.Equal(AdminAuditKinds.SignOut, audit.Entries[^1].Kind);
    }
}

public class DashboardShellTests
{
    private static readonly TerminalKey _enter = new(TerminalKeyKind.Enter);
    private static readonly TerminalKey _tab = new(TerminalKeyKind.Tab);
    private static readonly TerminalKey _right = new(TerminalKeyKind.Right);
    private static readonly TerminalKey _down = new(TerminalKeyKind.Down);

    private static (DashboardShell Shell, ScriptedTerminal Terminal, FakeKodo Kodo, FakeEvents Events, FakeSessions Sessions) Create(
        Func<ScriptedTerminal, ScriptedTerminal>? script = null, AdminIdentity? owner = null)
    {
        var terminal = script?.Invoke(new ScriptedTerminal(supportsKeys: true)) ?? new ScriptedTerminal(supportsKeys: true);
        var kodo = new FakeKodo();
        var events = new FakeEvents();
        var sessions = new FakeSessions();
        var clock = new FakeClock(Samples.Now);
        var signIn = new OpsSignIn(
            new FakeOwnerGate(owner ?? Samples.Owner), new FakeRedeemer("CODE", Samples.Owner), sessions, new FakeAuthorizer(true), new RecordingAudit(), clock);
        var ports = new OpsPorts(Status: new FakeStatus(Samples.Status()), Kodo: kodo, Events: events, Logs: new FakeLogs(
            new OpsLogLine(Samples.Now, OpsLogLevel.Information, "gw", "hello")));
        return (new DashboardShell(terminal, ports, signIn, TuiStyle.Plain, clock, TimeZoneInfo.Utc), terminal, kodo, events, sessions);
    }

    [Fact]
    public async Task The_default_screen_is_the_health_overview_and_fits_24_lines()
    {
        var (shell, _, _, _, _) = Create();

        var frame = await shell.RenderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultMenus.Health, shell.CurrentScreen);
        Assert.True(frame.Lines.Count <= 24);
        Assert.All(frame.Lines, l => Assert.True(l.Length <= 80));
        Assert.Contains("[Health]", frame.Lines[0]);
        Assert.Contains(frame.Lines, l => l.Contains("Server: Bazaar 0.1.1"));
        Assert.Contains(frame.Lines, l => l.Contains("Nothing needs attention."));
    }

    [Fact]
    public async Task The_menu_bar_has_the_decided_top_level_menus()
    {
        var (shell, _, _, _, _) = Create();

        var top = (await shell.RenderAsync(TestContext.Current.CancellationToken)).Lines[0];

        foreach (var title in new[] { "Health", "Kodo", "Logs", "Events", "Admin" })
        {
            Assert.Contains(title, top);
        }

        Assert.DoesNotContain("Modules", top);
    }

    [Fact]
    public async Task Arrow_keys_and_enter_move_to_another_screen()
    {
        var (shell, _, _, _, _) = Create();

        shell.HandleKey(_right);
        shell.HandleKey(_enter);

        Assert.Equal(DefaultMenus.Kodo, shell.CurrentScreen);
        Assert.Contains("Kodo health", (await shell.RenderAsync(TestContext.Current.CancellationToken)).Lines[1]);
    }

    [Fact]
    public async Task The_admin_menu_opens_and_reaches_rebuilds_by_shortcut()
    {
        var (shell, _, _, _, _) = Create();

        shell.HandleKey(TerminalKey.Of('a'));
        var open = await shell.RenderAsync(TestContext.Current.CancellationToken);
        shell.HandleKey(TerminalKey.Of('r'));

        Assert.Contains(open.Lines, l => l.Contains("Modules") && l.Contains("Rebuilds"));
        Assert.Equal(DefaultMenus.Rebuilds, shell.CurrentScreen);
    }

    [Fact]
    public async Task Type_to_search_jumps_to_the_first_match()
    {
        var (shell, _, _, _, _) = Create(t => t.Lines("audit"));

        shell.HandleKey(TerminalKey.Of('/'));

        Assert.Equal(DefaultMenus.Audit, shell.CurrentScreen);
        Assert.NotNull(await shell.RenderAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_serious_failure_shows_a_banner_and_rings_the_bell_exactly_once()
    {
        var (shell, _, kodo, _, _) = Create();
        kodo.View = new AdminKodoView(10, [new("db", "failing", "lost")], []);

        var first = await shell.RenderAsync(TestContext.Current.CancellationToken);
        var second = await shell.RenderAsync(TestContext.Current.CancellationToken);

        Assert.True(first.Bell);
        Assert.False(second.Bell);
        Assert.StartsWith("!! SERIOUS: db is failing: lost", first.Lines[1]);
        Assert.StartsWith("!! SERIOUS", second.Lines[1]);
        Assert.True(second.Lines.Count <= 24);
    }

    [Fact]
    public async Task The_banner_goes_on_tab_and_stays_gone_while_the_failure_stays()
    {
        var (shell, _, kodo, _, _) = Create();
        kodo.View = new AdminKodoView(10, [new("db", "failing", null)], []);
        await shell.RenderAsync(TestContext.Current.CancellationToken);

        shell.HandleKey(_tab);
        var after = await shell.RenderAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(after.Lines, l => l.StartsWith("!!", StringComparison.Ordinal));
        Assert.False(after.Bell);
        Assert.Contains(after.Lines, l => l.StartsWith("[SERIOUS] db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_minor_failure_is_an_amber_line_with_no_banner_and_no_bell()
    {
        var (shell, _, kodo, _, _) = Create();
        kodo.View = new AdminKodoView(80, [new("cache", "degraded", "slow")], []);

        var frame = await shell.RenderAsync(TestContext.Current.CancellationToken);

        Assert.False(frame.Bell);
        Assert.DoesNotContain(frame.Lines, l => l.StartsWith("!!", StringComparison.Ordinal));
        Assert.Contains(frame.Lines, l => l.StartsWith("[minor] cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_major_win_shows_a_green_banner_until_dismissed_and_small_fixes_only_a_line()
    {
        var (shell, _, _, events, _) = Create();
        events.Events.Add(Samples.Win("big", OpsEventKind.CrashSurvived, major: true));
        events.Events.Add(Samples.Win("small", at: Samples.Now.AddMinutes(-20)));

        var frame = await shell.RenderAsync(TestContext.Current.CancellationToken);
        shell.HandleKey(_tab);
        var after = await shell.RenderAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("FIXED: fixed big", frame.Lines[1]);
        Assert.Contains(frame.Lines, l => l.Contains("[self-healed] fixed small"));
        Assert.Contains(frame.Lines, l => l.Contains("2 things fixed themselves today"));
        Assert.DoesNotContain(after.Lines, l => l.StartsWith("FIXED:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Alerts_show_on_every_screen()
    {
        var (shell, _, kodo, _, _) = Create();
        kodo.View = new AdminKodoView(10, [new("db", "failing", null)], []);
        shell.HandleKey(TerminalKey.Of('l'));

        var frame = await shell.RenderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultMenus.Logs, shell.CurrentScreen);
        Assert.StartsWith("!! SERIOUS", frame.Lines[1]);
        Assert.Contains(frame.Lines, l => l.Contains("hello"));
    }

    [Fact]
    public async Task Log_filter_keys_change_the_level_and_module()
    {
        var (shell, _, _, _, _) = Create(t => t.Lines("gw"));
        shell.HandleKey(TerminalKey.Of('l'));

        shell.HandleKey(TerminalKey.Of(']'));
        shell.HandleKey(TerminalKey.Of('f'));

        Assert.Equal(OpsLogLevel.Warning, shell.LogsFilter.MinLevel);
        Assert.Equal("gw", shell.LogsFilter.Module);
        Assert.Contains("level Warning", (await shell.RenderAsync(TestContext.Current.CancellationToken)).Lines[1]);
    }

    [Fact]
    public async Task Run_signs_in_as_owner_draws_and_leaves_on_cancel_with_the_session_revoked()
    {
        var (shell, terminal, _, _, sessions) = Create(t => t.Keys(_enter, new TerminalKey(TerminalKeyKind.Cancel)));

        var code = await shell.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("[Health]", terminal.Output);
        Assert.Equal(0, sessions.LiveCount);
    }

    [Fact]
    public async Task Run_redraws_in_place_between_keys()
    {
        var (shell, terminal, _, _, _) = Create(t => t.Keys(_enter, _right, new TerminalKey(TerminalKeyKind.Cancel)));

        await shell.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(terminal.ErasedLines > 0);
    }

    [Fact]
    public async Task Run_with_an_admin_code_does_not_echo_the_code()
    {
        var (shell, terminal, _, _, sessions) = Create(
            t => t.Keys(_down, _enter).Keys("CODE".Select(TerminalKey.Of).ToArray()).Keys(_enter, new TerminalKey(TerminalKeyKind.Cancel)));

        var code = await shell.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.DoesNotContain("CODE", terminal.Output.Replace("admin code", string.Empty, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, sessions.LiveCount);
    }

    [Fact]
    public async Task Run_gives_up_when_sign_in_is_abandoned()
    {
        var (shell, terminal, _, _, _) = Create(t => t.Keys(new TerminalKey(TerminalKeyKind.Cancel)));

        Assert.Equal(1, await shell.RunAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("[Health]", terminal.Output);
    }

    [Fact]
    public async Task A_revoked_session_ends_the_screen_and_asks_to_sign_in_again()
    {
        var (shell, terminal, _, _, sessions) = Create(t => t.Keys(_enter, _right, new TerminalKey(TerminalKeyKind.Cancel)));
        sessions.RevokeAfterValidations = 1;

        var code = await shell.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.Contains("Your session ended", terminal.Output);
    }

    [Fact]
    public async Task Run_refuses_a_terminal_without_keys()
    {
        var plain = new ScriptedTerminal(supportsKeys: false);
        var other = new DashboardShell(plain, new OpsPorts(), new OpsSignIn(null, null, new FakeSessions(), new FakeAuthorizer(true), new RecordingAudit(), TimeProvider.System), TuiStyle.Plain);

        Assert.Equal(2, await other.RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("single keys", plain.Output);
    }
}
