using Xunit;

namespace Bazaar.Server.Providers.ClaudeCode.Tests;

public sealed class OrphanSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-proc-" + Guid.NewGuid().ToString("N"));

    public OrphanSweepTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Proc(int pid, uint uid, string environ)
    {
        var dir = Path.Combine(_root, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "status"), $"Name:\tclaude\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\n");
        File.WriteAllText(Path.Combine(dir, "environ"), environ);
    }

    private static string Env(string marker) => $"PATH=/bin\0{ClaudeCodeOrphanMarker.EnvName}={marker}\0HOME=/h\0";

    [Fact]
    public void Kills_only_matching_marker_of_own_user_and_never_self()
    {
        Proc(100, 1000, Env("cc-a"));
        Proc(101, 1000, Env("cc-b"));
        Proc(102, 2000, Env("cc-a"));
        Proc(103, 1000, Env("cc-a"));
        Proc(104, 1000, "PATH=/bin\0");
        var killed = new List<int>();
        var result = LinuxOrphanSweeper.Sweep(_root, "cc-a", selfPid: 103, ownUid: 1000, killed.Add);
        Assert.Equal([100], result);
        Assert.Equal([100], killed);
    }

    [Fact]
    public void Missing_proc_root_kills_nothing() =>
        Assert.Empty(LinuxOrphanSweeper.Sweep(Path.Combine(_root, "none"), "x", 5, 1000, _ => throw new InvalidOperationException()));

    [Fact]
    public void Marker_is_stable_and_configurable()
    {
        var a = new ClaudeCodeOptions { ConfigDir = Path.Combine(_root, "login") };
        Assert.Equal(ClaudeCodeOrphanMarker.For(a), ClaudeCodeOrphanMarker.For(a));
        Assert.StartsWith("cc-", ClaudeCodeOrphanMarker.For(a), StringComparison.Ordinal);
        Assert.Equal("mine", ClaudeCodeOrphanMarker.For(new ClaudeCodeOptions { RunMarker = " mine " }));
    }
}
