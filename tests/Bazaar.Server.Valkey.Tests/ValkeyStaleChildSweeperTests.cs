using System.Diagnostics;

namespace Bazaar.Server.Valkey.Tests;

/// <summary>The stale-child sweep: a pid file from an earlier host kills only a process still named as the server.</summary>
public class ValkeyStaleChildSweeperTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-valkey-sweep-" + Guid.NewGuid().ToString("N"));

    public ValkeyStaleChildSweeperTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Missing_pid_file_means_nothing_to_sweep()
    {
        var probe = new FakeProbe();

        var killed = new ValkeyStaleChildSweeper(probe).Sweep(PidFile(), "valkey-server");

        Assert.False(killed);
        Assert.Empty(probe.Killed);
    }

    [Fact]
    public void A_stale_server_named_in_the_pid_file_is_killed_and_the_file_removed()
    {
        var probe = new FakeProbe { Named = [777] };
        File.WriteAllText(PidFile(), "777\n");

        var killed = new ValkeyStaleChildSweeper(probe).Sweep(PidFile(), "valkey-server");

        Assert.True(killed);
        Assert.Equal([777], probe.Killed);
        Assert.False(File.Exists(PidFile()));
    }

    [Fact]
    public void A_pid_reused_by_another_program_is_not_killed_but_the_file_is_removed()
    {
        var probe = new FakeProbe { Named = [] };
        File.WriteAllText(PidFile(), "777");

        var killed = new ValkeyStaleChildSweeper(probe).Sweep(PidFile(), "valkey-server");

        Assert.False(killed);
        Assert.Empty(probe.Killed);
        Assert.False(File.Exists(PidFile()));
    }

    [Fact]
    public void Unreadable_content_is_not_a_pid_so_nothing_is_killed()
    {
        var probe = new FakeProbe { Named = [0] };
        File.WriteAllText(PidFile(), "not-a-pid");

        var killed = new ValkeyStaleChildSweeper(probe).Sweep(PidFile(), "valkey-server");

        Assert.False(killed);
        Assert.Empty(probe.Killed);
        Assert.False(File.Exists(PidFile()));
    }

    [Fact]
    public void Linux_probe_recognises_this_process_by_its_name_and_no_other()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var probe = new LinuxValkeyProcessProbe();
        var name = Process.GetCurrentProcess().ProcessName;

        Assert.True(probe.IsNamed(Environment.ProcessId, name));
        Assert.False(probe.IsNamed(Environment.ProcessId, "no-such-program"));
        Assert.False(probe.IsNamed(0, name));
    }

    private string PidFile() => Path.Combine(_directory, ValkeyPidFile.FileName);

    private sealed class FakeProbe : IValkeyProcessProbe
    {
        public HashSet<int> Named { get; init; } = [];

        public List<int> Killed { get; } = [];

        public bool IsNamed(int pid, string name) => Named.Contains(pid);

        public void Kill(int pid) => Killed.Add(pid);
    }
}
