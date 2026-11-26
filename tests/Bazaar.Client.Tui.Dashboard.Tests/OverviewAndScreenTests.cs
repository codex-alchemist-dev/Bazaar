using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard.Tests;

public class HealthOverviewTests
{
    private static HealthOverview Build(AdminKodoView? kodo, IEnumerable<OpsEvent>? events = null, bool status = true) =>
        HealthOverviewBuilder.Build(status ? Samples.Status() : null, kodo, events ?? [], Samples.Now, TimeZoneInfo.Utc);

    [Fact]
    public void Summary_counts_checks_by_grade()
    {
        var kodo = new AdminKodoView(88,
            [new("a", "healthy", null), new("b", "degraded", "slow"), new("c", "failing", "no db")],
            []);

        var overview = Build(kodo);

        Assert.Equal("Kodo: score 88, 1 ok, 1 minor, 1 serious", overview.KodoLine);
        Assert.Equal(OpsSeverity.Serious, overview.Attention[0].Severity);
        Assert.Contains("c is failing: no db", overview.Attention[0].Text);
        Assert.Single(overview.SeriousItems);
    }

    [Fact]
    public void An_informational_incident_is_not_attention_but_a_critical_one_is_serious()
    {
        var kodo = new AdminKodoView(null, [],
            [new("i1", "Fyi", "info", Samples.Now, null), new("i2", "Disk gone", "critical", Samples.Now, "rb://disk")]);

        var overview = Build(kodo);

        var line = Assert.Single(overview.Attention);
        Assert.Equal(OpsSeverity.Serious, line.Severity);
        Assert.Equal("incident:i2", line.Key);
    }

    [Fact]
    public void Daily_summary_counts_only_wins_that_fixed_something_today()
    {
        OpsEvent[] events =
        [
            Samples.Win("a"),
            Samples.Win("b", OpsEventKind.RestartWorked),
            Samples.Win("backup", OpsEventKind.BackupVerified),
            Samples.Win("old", at: Samples.Now.AddDays(-1)),
            new("fault", Samples.Now, OpsEventKind.Fault, "broke"),
        ];

        Assert.Equal("2 things fixed themselves today", Build(new AdminKodoView(null, [], []), events).DailySummary);
        Assert.Equal("1 thing fixed itself today", HealthOverviewBuilder.DailySummary([Samples.Win("a")], Samples.Now, TimeZoneInfo.Utc));
        Assert.Null(HealthOverviewBuilder.DailySummary([], Samples.Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Daily_summary_uses_the_operators_day_not_UTC()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus8", TimeSpan.FromHours(8), "plus8", "plus8");
        var now = new DateTimeOffset(2026, 11, 22, 23, 0, 0, TimeSpan.Zero);
        var sixHoursEarlier = Samples.Win("early", at: now.AddHours(-6));

        // 17:00 UTC on the 22nd is 01:00 on the 23rd in +8, the same local day as now (07:00 on the 23rd), though a different UTC day.
        Assert.Equal("1 thing fixed itself today", HealthOverviewBuilder.DailySummary([sixHoursEarlier], now, zone));
        Assert.Null(HealthOverviewBuilder.DailySummary([sixHoursEarlier], now.AddHours(25), zone));
    }

    [Fact]
    public void A_crash_of_the_tui_shows_as_a_fault_line()
    {
        var crash = HealthOverviewBuilder.CrashEvent(new OpsCrashNote(Samples.Now, "boom\u001b[31m"));

        Assert.Equal(OpsEventKind.Fault, crash.Kind);
        Assert.Contains("Resident TUI crashed", crash.Summary);
        Assert.DoesNotContain('\u001b', crash.Summary);
    }

    [Fact]
    public void Missing_ports_read_as_unavailable_not_as_healthy()
    {
        var overview = Build(null, status: false);
        var lines = HealthOverviewRenderer.Render(overview, Samples.Context());

        Assert.Contains("Server: unavailable", lines);
        Assert.Contains("Kodo: unavailable", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Nothing needs attention"));
    }

    [Fact]
    public void A_busy_overview_fits_80_by_24_with_no_overflowing_line()
    {
        var checks = Enumerable.Range(0, 40).Select(i => new AdminKodoCheck("check" + i, i % 3 == 0 ? "failing" : "degraded", new string('x', 200))).ToList();
        var events = Enumerable.Range(0, 60).Select(i => Samples.Win("e" + i, at: Samples.Now.AddMinutes(-i))).ToList();
        var overview = Build(new AdminKodoView(50, checks, []), events);

        var lines = HealthOverviewRenderer.Render(overview, Samples.Context());

        Assert.True(lines.Count <= 24, "overview used " + lines.Count + " lines");
        Assert.All(lines, line => Assert.True(line.Length <= 80, line));
        Assert.Contains(lines, l => l.Contains("more, see Kodo"));
        Assert.Contains(lines, l => l.Contains("fixed themselves today"));
    }

    [Fact]
    public void Serious_and_minor_lines_carry_a_word_with_or_without_colour()
    {
        var kodo = new AdminKodoView(null, [new("db", "failing", null), new("cache", "degraded", null)], []);
        var overview = Build(kodo);

        var plain = HealthOverviewRenderer.Render(overview, Samples.Context());
        var coloured = HealthOverviewRenderer.Render(overview, Samples.Context(color: true));

        Assert.Contains(plain, l => l.StartsWith("[SERIOUS] db", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.StartsWith("[minor] cache", StringComparison.Ordinal));
        Assert.DoesNotContain(plain, l => l.Contains('\u001b'));
        Assert.Contains(coloured, l => l.Contains("\u001b[31m") && l.Contains("[SERIOUS]"));
        Assert.Contains(coloured, l => l.Contains("\u001b[33m") && l.Contains("[minor]"));
    }

    [Fact]
    public void Wins_are_green_and_also_say_so_in_words()
    {
        var overview = Build(new AdminKodoView(null, [], []), [Samples.Win("a", OpsEventKind.SlotRollback)]);

        var coloured = HealthOverviewRenderer.Render(overview, Samples.Context(color: true));
        var plain = HealthOverviewRenderer.Render(overview, Samples.Context());

        Assert.Contains(coloured, l => l.Contains("\u001b[32m") && l.Contains("[rolled back]"));
        Assert.Contains(plain, l => l.Contains("[rolled back] fixed a"));
    }
}

public class OpsScreenTests
{
    private static readonly LogsFilterState _noFilter = new();

    [Fact]
    public async Task A_screen_with_no_port_says_unavailable_and_others_still_work()
    {
        var ports = new OpsPorts(Kodo: new FakeKodo { Throw = true }, Events: new FakeEvents(Samples.Win("a")));

        var kodo = await OpsScreens.LoadAsync(DefaultMenus.Kodo, ports, Samples.Context(), _noFilter, TestContext.Current.CancellationToken);
        var events = await OpsScreens.LoadAsync(DefaultMenus.Events, ports, Samples.Context(), _noFilter, TestContext.Current.CancellationToken);
        var audit = await OpsScreens.LoadAsync(DefaultMenus.Audit, ports, Samples.Context(), _noFilter, TestContext.Current.CancellationToken);

        Assert.Contains(kodo, l => l.Contains("unavailable"));
        Assert.Contains(events, l => l.Contains("fixed a"));
        Assert.Contains(audit, l => l.Contains("unavailable"));
    }

    [Fact]
    public async Task Kodo_screen_lists_checks_worst_first_with_incident_runbook_links()
    {
        var kodo = new FakeKodo
        {
            View = new(70, [new("ok", "healthy", null), new("bad", "failing", "x")], [new("i1", "Db down", "critical", Samples.Now, "rb://db")]),
        };

        var lines = await OpsScreens.KodoAsync(new OpsPorts(Kodo: kodo), Samples.Context(), TestContext.Current.CancellationToken);

        Assert.True(lines.ToList().FindIndex(l => l.Contains("bad")) < lines.ToList().FindIndex(l => l.Contains("ok healthy")));
        Assert.Contains(lines, l => l.Contains("runbook rb://db"));
    }

    [Fact]
    public async Task Log_text_is_cleaned_before_it_is_drawn()
    {
        var logs = new FakeLogs(new OpsLogLine(Samples.Now, OpsLogLevel.Error, "gw", "bad\u001b[2J\nsecond line\a"));

        var lines = await OpsScreens.LogsAsync(new OpsPorts(Logs: logs), Samples.Context(), _noFilter, TestContext.Current.CancellationToken);

        Assert.All(lines, l => Assert.DoesNotContain('\u001b', l));
        Assert.Contains(lines, l => l.Contains("bad.[2J second line."));
    }

    [Fact]
    public async Task Log_filters_by_level_and_module_reach_the_port()
    {
        var logs = new FakeLogs(
            new OpsLogLine(Samples.Now, OpsLogLevel.Information, "gw", "info line"),
            new OpsLogLine(Samples.Now, OpsLogLevel.Error, "gw", "error line"),
            new OpsLogLine(Samples.Now, OpsLogLevel.Error, "db", "db error"));
        var filter = new LogsFilterState { Module = "gw" };
        filter.Raise();
        filter.Raise();

        var lines = await OpsScreens.LogsAsync(new OpsPorts(Logs: logs), Samples.Context(), filter, TestContext.Current.CancellationToken);

        Assert.Equal(OpsLogLevel.Error, logs.LastFilter!.MinLevel);
        Assert.Equal("gw", logs.LastFilter.Module);
        Assert.Contains(lines, l => l.Contains("error line"));
        Assert.DoesNotContain(lines, l => l.Contains("info line") || l.Contains("db error"));
    }

    [Fact]
    public async Task A_log_longer_than_the_screen_keeps_its_newest_lines()
    {
        var lines = Enumerable.Range(0, 100).Select(i => new OpsLogLine(Samples.Now, OpsLogLevel.Information, "m", "line" + i)).ToArray();

        var shown = await OpsScreens.LogsAsync(new OpsPorts(Logs: new FakeLogs(lines)), Samples.Context(rows: 10), _noFilter, TestContext.Current.CancellationToken);

        Assert.True(shown.Count <= 10);
        Assert.Contains(shown, l => l.Contains("line99"));
        Assert.DoesNotContain(shown, l => l.Contains("line0 "));
    }

    [Fact]
    public async Task Events_merge_the_crash_journal_newest_first()
    {
        var crashes = new FakeCrashJournal();
        await crashes.RecordAsync(new OpsCrashNote(Samples.Now, "oom"), TestContext.Current.CancellationToken);
        var ports = new OpsPorts(Events: new FakeEvents(Samples.Win("old", at: Samples.Now.AddHours(-1))), Crashes: crashes);

        var events = await OpsScreens.ReadEventsAsync(ports, 10, TestContext.Current.CancellationToken);

        Assert.NotNull(events);
        Assert.Contains("crashed", events[0].Summary);
        Assert.Equal("old", events[1].Id);
    }

    [Fact]
    public void The_logs_filter_stays_within_the_levels()
    {
        var filter = new LogsFilterState();
        for (var i = 0; i < 10; i++)
        {
            filter.Raise();
        }

        Assert.Equal(OpsLogLevel.Critical, filter.MinLevel);
        for (var i = 0; i < 10; i++)
        {
            filter.Lower();
        }

        Assert.Equal(OpsLogLevel.Trace, filter.MinLevel);
    }

    [Fact]
    public async Task Users_screen_marks_revoked_with_a_word()
    {
        var users = new FakeUsers([new AdminUser(Guid.NewGuid(), "Ana", "admin", false, false), new AdminUser(Guid.NewGuid(), "Bot", "member", true, true)]);

        var lines = await OpsScreens.UsersAsync(new OpsPorts(Users: users), Samples.Context(), TestContext.Current.CancellationToken);

        Assert.Contains(lines, l => l.Contains("Ana | admin | person | active"));
        Assert.Contains(lines, l => l.Contains("Bot | member | agent | REVOKED"));
    }

    [Fact]
    public async Task Rebuilds_and_modules_read_through_the_module_selection_port()
    {
        var port = new FakeModules();

        var rebuilds = await OpsScreens.RebuildsAsync(new OpsPorts(Modules: port), Samples.Context(), TestContext.Current.CancellationToken);
        var modules = await OpsScreens.ModulesAsync(new OpsPorts(Modules: port), Samples.Context(), TestContext.Current.CancellationToken);

        Assert.Contains(rebuilds, l => l.Contains("b-1 succeeded (attempt 1) | all green"));
        Assert.Contains(modules, l => l.Contains("[x] Kodo 1.0"));
        Assert.Contains(modules, l => l.Contains("[ ] Extra 2.0"));
    }

    private sealed class FakeUsers(IReadOnlyList<AdminUser> users) : IAdminUserDirectory
    {
        public Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken ct = default) => Task.FromResult(users);
    }

    private sealed class FakeModules : IAdminModuleSelection
    {
        public Task<AdminModuleCatalogue> GetCatalogueAsync(CancellationToken ct = default) =>
            Task.FromResult(new AdminModuleCatalogue(3, [new("kodo", "Kodo", "1.0", "stable", [], true), new("extra", "Extra", "2.0", "stable", [], false)]));

        public Task<IReadOnlyList<AdminBuildRecord>> GetBuildsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AdminBuildRecord>>([new("b-1", "succeeded", Samples.Now, Samples.Now, "all green", 1)]);

        public Task<AdminSelectionPreview> PreviewAsync(IReadOnlyList<string> selectedModuleIds, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AdminSelectionCommit> CommitAsync(Guid actorPrincipalId, IReadOnlyList<string> selectedModuleIds, long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AdminRebuildStart> StartRebuildAsync(Guid actorPrincipalId, string idempotencyKey, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
