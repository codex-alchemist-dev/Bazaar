using System.Diagnostics;
using Bazaar.Server.Valkey;

namespace Bazaar.Server.Garnet.Tests;

/// <summary>The stale-child sweep: a pid file from an earlier host kills only a process still named as the server.</summary>
public class GarnetStaleChildSweeperTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-garnet-sweep-" + Guid.NewGuid().ToString("N"));

    public GarnetStaleChildSweeperTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Missing_pid_file_means_nothing_to_sweep()
    {
        var probe = new FakeProbe();

        var killed = new GarnetStaleChildSweeper(probe).Sweep(PidFile(), "Garnet.exe");
        var killed = new GarnetStaleChildSweeper(probe).Sweep(PidFile(), "garnet-server.exe");

        Assert.False(killed);
        Assert.Empty(probe.Killed);
    }

    [Fact]
    public void A_stale_server_named_in_the_pid_file_is_killed_and_the_file_removed()
    {
        var probe = new FakeProbe { Named = [777] };
        File.WriteAllText(PidFile(), "777\n");

        var killed = new GarnetStaleChildSweeper(probe).Sweep(PidFile(), "Garnet.exe");
        var killed = new GarnetStaleChildSweeper(probe).Sweep(PidFile(), "garnet-server.exe");

        Assert.True(killed);
        Assert.Equal([777], probe.Killed);
        Assert.False(File.Exists(PidFile()));
    }

    [Fact]
    public void A_pid_reused_by_another_program_is_not_killed_but_the_file_is_removed()
    {
        var probe = new FakeProbe { Named = [] };
        File.WriteAllText(PidFile(), "777");

        var killed = new GarnetStaleChildSweeper(probe).Sweep(PidFile(), "Garnet.exe");
        var killed = new GarnetStaleChildSweeper(probe).Sweep(PidFile(), "garnet-server.exe");

        Assert.False(killed);
        Assert.Empty(probe.Killed);
        Assert.False(File.Exists(PidFile()));
    }

    [Fact]
    public void Unreadable_content_is_not_a_pid_so_nothing_is_killed()
    {
        var probe = new FakeProbe { Named = [0] };
        File.WriteAllText(PidFile(), "not-a-pid");

        var killed = new GarnetStaleChildSweeper(probe).Sweep(PidFile(), "Garnet.exe");
        var killed = new GarnetStaleChildSweeper(probe).Sweep(PidFile(), "garnet-server.exe");

        Assert.False(killed);
        Assert.Empty(probe.Killed);
        Assert.False(File.Exists(PidFile()));
    }

    [Fact]
    public void Process_probe_recognises_this_process_by_its_name_without_extension_and_no_other()
    {
        var probe = new ProcessGarnetProcessProbe();
        var name = Process.GetCurrentProcess().ProcessName;

        Assert.True(probe.IsNamed(Environment.ProcessId, name));
        Assert.True(probe.IsNamed(Environment.ProcessId, name.ToUpperInvariant() + ".exe"));
        Assert.False(probe.IsNamed(Environment.ProcessId, "no-such-program"));
        Assert.False(probe.IsNamed(0, name));
    }

    private string PidFile() => Path.Combine(_directory, GarnetPidFile.FileName);

    private sealed class FakeProbe : IGarnetProcessProbe
    {
        public HashSet<int> Named { get; init; } = [];

        public List<int> Killed { get; } = [];

        public bool IsNamed(int pid, string name) => Named.Contains(pid);

        public void Kill(int pid) => Killed.Add(pid);
    }
}
