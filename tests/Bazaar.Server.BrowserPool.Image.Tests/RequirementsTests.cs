using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.Image.Tests;

public class RequirementsTests
{
    private static IReadOnlyList<PoolImageRequirement> Items(BrowserPoolOptions? pool = null, LinuxPoolHostOptions? host = null) =>
        PoolImageRequirements.From(pool ?? new BrowserPoolOptions(), host ?? new LinuxPoolHostOptions()).Items;

    [Fact]
    public void Defaults_list_the_per_slot_namespace_setup_in_a_fixed_order()
    {
        var ids = Items().Select(i => i.Id).ToArray();

        Assert.Equal(
            [
                "program:chromium", "program:bwrap", "program:setpriv", "program:cage", "program:ip", "program:nsenter", "program:sysctl",
                "program:nft", "program:systemd-run", "program:probe-setpriv", "program:probe-nsenter", "directory:slot-root",
                "apparmor:bazaar-pool-bwrap",
            ],
            ids);
    }

    [Fact]
    public void The_order_is_deterministic()
    {
        Assert.Equal(Items(), Items());
    }

    [Fact]
    public void Paths_come_from_the_options()
    {
        var pool = new BrowserPoolOptions { ChromiumPath = "/opt/c/chrome", BwrapPath = "/opt/b/bwrap", SlotRoot = "/srv2/slots" };

        var items = Items(pool).ToDictionary(i => i.Id);

        Assert.Equal("/opt/c/chrome", items["program:chromium"].Path);
        Assert.Equal("/opt/b/bwrap", items["program:bwrap"].Path);
        Assert.Equal("/srv2/slots", items["directory:slot-root"].Path);
        Assert.Equal("/etc/apparmor.d/bazaar-pool-bwrap", items["apparmor:bazaar-pool-bwrap"].Path);
        Assert.Equal(PoolImageRequirementKind.AppArmorProfile, items["apparmor:bazaar-pool-bwrap"].Kind);
    }

    [Fact]
    public void Slot_execution_is_required_for_what_the_slot_runs()
    {
        var items = Items().ToDictionary(i => i.Id);

        Assert.True(items["program:chromium"].SlotsMustExecute);
        Assert.True(items["program:bwrap"].SlotsMustExecute);
        Assert.True(items["program:cage"].SlotsMustExecute);
        Assert.True(items["directory:slot-root"].SlotsMustExecute);
        Assert.False(items["program:setpriv"].SlotsMustExecute);
        Assert.False(items["program:nft"].SlotsMustExecute);
    }

    [Fact]
    public void Shared_namespace_drops_the_namespace_tools()
    {
        var ids = Items(new BrowserPoolOptions { NetworkIsolation = NetworkIsolation.SharedWithUidRule }).Select(i => i.Id).ToArray();

        Assert.DoesNotContain("program:ip", ids);
        Assert.DoesNotContain("program:nsenter", ids);
        Assert.DoesNotContain("program:sysctl", ids);
    }

    [Fact]
    public void Systemd_run_is_listed_only_when_used()
    {
        Assert.DoesNotContain(Items(new BrowserPoolOptions { UseSystemdRun = false }), i => i.Id == "program:systemd-run");
        Assert.Contains(Items(), i => i.Id == "program:systemd-run");
    }

    [Fact]
    public void The_pool_spawn_helper_brings_its_probe_tool_and_the_pipe_parent()
    {
        var host = new LinuxPoolHostOptions { PoolSpawnHelperPath = "/opt/bazaar/bazaar-pool-spawn" };

        var items = Items(host: host).ToDictionary(i => i.Id);

        Assert.Equal("/opt/bazaar/bazaar-pool-spawn", items["program:pool-spawn"].Path);
        // The probe tool defaults to the helper, so the one entry carries the slot requirement.
        Assert.True(items["program:pool-spawn"].SlotsMustExecute);
        Assert.DoesNotContain("program:probe-tool", items.Keys);
        Assert.Equal("/run/bazaar-pool", items["directory:pipe-parent"].Path);
        Assert.False(items["directory:pipe-parent"].SlotsMustExecute);
    }

    [Fact]
    public void A_probe_tool_equal_to_the_helper_is_listed_once_and_keeps_the_slot_requirement()
    {
        var host = new LinuxPoolHostOptions { PoolSpawnHelperPath = "/opt/bazaar/bazaar-pool-spawn" };

        var matches = Items(host: host).Where(i => i.Path == "/opt/bazaar/bazaar-pool-spawn").ToList();

        var only = Assert.Single(matches);
        Assert.Equal("program:pool-spawn", only.Id);
        Assert.True(only.SlotsMustExecute);
    }

    [Fact]
    public void A_separate_probe_tool_is_listed_with_slot_access()
    {
        var host = new LinuxPoolHostOptions { PoolSpawnHelperPath = "/opt/bazaar/bazaar-pool-spawn", ProbeToolPath = "/opt/bazaar/probe" };

        var items = Items(host: host).ToDictionary(i => i.Id);

        Assert.True(items["program:probe-tool"].SlotsMustExecute);
        Assert.False(items["program:pool-spawn"].SlotsMustExecute);
    }

    [Fact]
    public void Probe_off_drops_the_probe_entries()
    {
        var host = new LinuxPoolHostOptions { ProbeEnabled = false, PoolSpawnHelperPath = "/opt/bazaar/bazaar-pool-spawn" };

        var ids = Items(host: host).Select(i => i.Id).ToArray();

        Assert.DoesNotContain("program:probe-tool", ids);
        Assert.DoesNotContain("program:probe-setpriv", ids);
        Assert.DoesNotContain("program:probe-nsenter", ids);
    }

    [Fact]
    public void Equal_paths_are_listed_once()
    {
        // The host's setpriv and nsenter resolve to the same paths as the pool's.
        var pool = new BrowserPoolOptions();
        var host = new LinuxPoolHostOptions { SetprivPath = pool.SetprivPath, NsenterPath = pool.NsenterPath };

        var items = Items(pool, host);

        Assert.Equal(items.Count, items.Select(i => (i.Kind, i.Path)).Distinct().Count());
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
        Assert.DoesNotContain(items, i => i.Id == "program:probe-setpriv");
    }

    [Fact]
    public void A_pipe_directory_too_close_to_the_root_is_refused()
    {
        var host = new LinuxPoolHostOptions { PoolSpawnHelperPath = "/opt/bazaar/bazaar-pool-spawn", PipeDirectory = "/run/pipes" };

        Assert.Throws<ArgumentException>(() => PoolImageRequirements.From(new BrowserPoolOptions(), host));
    }
}
