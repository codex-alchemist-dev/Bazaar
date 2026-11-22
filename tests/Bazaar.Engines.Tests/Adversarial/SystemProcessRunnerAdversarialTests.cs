using Bazaar.Engines.Roblox.Rojo;

namespace Bazaar.Engines.Tests.Adversarial;

/// <summary>Attacks on <see cref="SystemProcessRunner"/>: output floods, a child that never exits and the server's environment.</summary>
public sealed class SystemProcessRunnerAdversarialTests
{
    private static string FakeAdapterPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "FakeAdapter.exe" : "FakeAdapter");

    [Fact]
    public async Task A_child_that_never_exits_is_killed_at_the_timeout()
    {
        var runner = new SystemProcessRunner();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // flood-line writes 20 MiB then sleeps forever: both a flood and a hang.
        var result = await runner.RunAsync(FakeAdapterPath, ["flood-line"], AppContext.BaseDirectory, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString());
        Assert.True(result.StandardOutput.Length <= SystemProcessRunner.MaxCapturedChars);
    }

    [Fact]
    public async Task Captured_output_is_capped_however_much_the_child_writes()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(FakeAdapterPath, ["flood-line"], AppContext.BaseDirectory, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(SystemProcessRunner.MaxCapturedChars, result.StandardOutput.Length);
    }

    [Fact]
    public async Task The_servers_secrets_do_not_reach_the_child()
    {
        // Adapter hosts and the Rojo runner share the allowlist; the adapter test below drives the child end to end.
        Assert.False(Bazaar.Engines.Processes.ChildProcessEnvironment.IsAllowed("ANTHROPIC_API_KEY"));
        Assert.False(Bazaar.Engines.Processes.ChildProcessEnvironment.IsAllowed("BAZAAR_DB_PASSWORD"));
        Assert.False(Bazaar.Engines.Processes.ChildProcessEnvironment.IsAllowed("GITHUB_TOKEN"));
        Assert.True(Bazaar.Engines.Processes.ChildProcessEnvironment.IsAllowed("path"));
        await Task.CompletedTask;
    }
}
