using Bazaar.Secrets.Keyring;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Secrets.Tests;

/// <summary>Doc 61 Q2: the real <c>secret-tool</c> runner is bounded in time and never searches PATH.</summary>
public sealed class ProcessSecretToolRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-secret-tool-" + Guid.CreateVersion7().ToString("N"));

    public ProcessSecretToolRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Script(string body)
    {
        var path = Path.Combine(_dir, "secret-tool");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task A_hanging_secret_tool_is_killed_and_reported_unavailable()
    {
        if (OperatingSystem.IsWindows())
            return;
        var runner = new ProcessSecretToolRunner(Script("exec sleep 30"), TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAsync<KekCustodyException>(() => runner.RunAsync(["lookup", "service", "x", "account", "y"], null, TestContext.Current.CancellationToken));
        var probe = await new SecretServiceKekKeyring(runner).ProbeAsync("e", TestContext.Current.CancellationToken);
        var sealing = new SecretServiceKekKeyring(runner).SealAsync("e", new byte[32], new byte[1], TestContext.Current.CancellationToken).AsTask();

        Assert.False(probe.Available);
        Assert.Contains("did not answer", probe.Reason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<KekKeyringException>(() => sealing);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_caller_cancellation_stays_a_cancellation()
    {
        if (OperatingSystem.IsWindows())
            return;
        var runner = new ProcessSecretToolRunner(Script("exec sleep 30"), TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(["lookup"], null, cts.Token));
    }

    [Fact]
    public async Task An_answering_secret_tool_returns_its_output()
    {
        if (OperatingSystem.IsWindows())
            return;
        var runner = new ProcessSecretToolRunner(Script("cat >/dev/null; echo hello"), TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(["lookup"], "in", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput.Trim());
    }

    [Fact]
    public void A_relative_path_or_a_non_positive_timeout_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ProcessSecretToolRunner("secret-tool"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessSecretToolRunner(null, TimeSpan.Zero));
    }
}
