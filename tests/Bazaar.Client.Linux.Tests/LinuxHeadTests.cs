using Avalonia.Headless.XUnit;
using Bazaar.Client.Linux;
using Bazaar.Client.Linux.Updates;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Linux.Tests;

/// <summary>The Linux head composes the shipped pane modules, shows one at a time and gives the palette its commands (doc 30 section 1).</summary>
public sealed class LinuxHeadTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "bazaar-linux-head-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LinuxHead Create(params string[] args)
    {
        var paths = new LinuxPaths(_state);
        var updates = UpdateHost.Create(_ => null, _state, _ => { }, TimeProvider.System, action => action());
        return LinuxHead.Create(new LinuxHeadOptions(null, paths, args, updates, _state, "test", _ => { }));
    }

    [AvaloniaFact]
    public async Task Registry_has_the_shipped_panes_with_unique_kinds()
    {
        await using var head = Create();

        string[] expected =
        [
            PaneKinds.Dashboard, PaneKinds.VaultSearch, PaneKinds.Org, PaneKinds.Calendar,
            PaneKinds.InterruptStack, PaneKinds.ColdArchive, PaneKinds.Terminal,
            PaneKinds.AgentAnalytics, PaneKinds.GitSolarSystem, "git.view", "git.prs", "git.pr", "git.diff",
        ];
        foreach (var kind in expected)
        {
            Assert.True(head.Registry.Contains(kind), kind);
        }

        Assert.Equal(head.Registry.Kinds.Count, head.Registry.Kinds.Distinct(StringComparer.Ordinal).Count());
    }

    [AvaloniaFact]
    public async Task Opens_on_the_dashboard_and_navigates_between_panes()
    {
        await using var head = Create();
        Assert.Equal(PaneKinds.Dashboard, head.ShownKind);

        await head.RunCommandAsync("nav.org");
        Assert.Equal(PaneKinds.Org, head.ShownKind);

        await head.RunCommandAsync("nav.calendar");
        Assert.Equal(PaneKinds.Calendar, head.ShownKind);
    }

    [AvaloniaFact]
    public async Task Navigates_to_the_analytics_solar_system_and_git_panes()
    {
        await using var head = Create();

        await head.RunCommandAsync("nav.analytics");
        Assert.Equal(PaneKinds.AgentAnalytics, head.ShownKind);

        await head.RunCommandAsync("nav.git.solar");
        Assert.Equal(PaneKinds.GitSolarSystem, head.ShownKind);

        await head.RunCommandAsync("nav.git");
        Assert.Equal("git.view", head.ShownKind);
    }

    [AvaloniaFact]
    public async Task Cold_archive_flag_opens_the_cold_archive_pane()
    {
        await using var head = Create("--cold-archive");

        Assert.Equal(PaneKinds.ColdArchive, head.ShownKind);
    }

    [AvaloniaFact]
    public async Task Palette_lists_the_head_commands()
    {
        await using var head = Create();

        var ids = head.Loaded.Commands.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in new[] { "nav.dashboard", "nav.org", "nav.analytics", "nav.git.solar", "nav.git", "update.check", "update.restart", "app.quit", "supportbundle.save" })
        {
            Assert.Contains(id, ids);
        }

        Assert.Contains(ids, id => id.StartsWith("defaultapp.", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.StartsWith("defaultterminal.", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Preference_file_lives_under_the_state_directory()
    {
        await using var head = Create();

        Assert.Equal(Path.Combine(_state, "preferences.json"), new LinuxPaths(_state).PreferencesFile);
        Assert.NotNull(head.Preferences);
    }
}
