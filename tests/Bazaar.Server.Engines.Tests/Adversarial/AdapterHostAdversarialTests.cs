using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Server.Engines.Adapters;

namespace Bazaar.Server.Engines.Tests.Adversarial;

/// <summary>A hostile adapter child: floods, forged ids, double replies, garbage, and the server's environment.</summary>
public sealed class AdapterHostAdversarialTests
{
    private const string _projectId = "project-a";

    private static string FakeAdapterPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "FakeAdapter.exe" : "FakeAdapter");

    private static AdapterHost Start(string mode, TimeSpan timeout, int maxMessageChars = 8 * 1024 * 1024, IReadOnlyDictionary<string, string>? environment = null) =>
        AdapterHost.Start(new AdapterProcessOptions(FakeAdapterPath, [mode], timeout) { MaxMessageChars = maxMessageChars, Environment = environment });

    private static EngineCommand Command(string id) =>
        new(id, "echo", JsonSerializer.SerializeToElement(new { }, EngineJson.Options));

    [Fact]
    public async Task An_oversize_line_with_no_newline_fails_the_session_and_kills_the_child()
    {
        await using var host = Start("flood-line", TimeSpan.FromSeconds(30), maxMessageChars: 1024 * 1024);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<EngineAdapterException>(() => host.OpenAsync(_projectId, CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "the flood must be cut at the cap, not at the command timeout");
        Assert.Equal(AdapterHostState.Failed, host.State);
        Assert.Contains("longer than", error.Message, StringComparison.Ordinal);
        Assert.All(host.RecentOutput(), line => Assert.True(line.Length <= 8192));
    }

    [Fact]
    public async Task A_reply_with_an_unknown_request_id_is_ignored_and_the_real_reply_still_wins()
    {
        await using var host = Start("wrong-id", TimeSpan.FromSeconds(30));
        await host.OpenAsync(_projectId, CancellationToken.None);

        var result = await host.CommandAsync(_projectId, Command("cmd-1"), CancellationToken.None);

        Assert.Equal("echo line 1\necho line 2", result.Output);
        Assert.NotEqual("forged", result.Output);
        Assert.Equal(AdapterHostState.Running, host.State);
    }

    [Fact]
    public async Task A_second_reply_to_the_same_request_cannot_replace_the_first()
    {
        await using var host = Start("double-reply", TimeSpan.FromSeconds(30));
        await host.OpenAsync(_projectId, CancellationToken.None);

        var first = await host.CommandAsync(_projectId, Command("cmd-1"), CancellationToken.None);
        var second = await host.CommandAsync(_projectId, Command("cmd-2"), CancellationToken.None);

        Assert.Equal("echo line 1\necho line 2", first.Output);
        Assert.Equal("cmd-2", second.CommandId);
        Assert.Equal("echo line 1\necho line 2", second.Output);
        Assert.Equal(AdapterHostState.Running, host.State);
    }

    [Fact]
    public async Task Invalid_utf8_and_non_json_lines_are_dropped_without_failing_the_session()
    {
        await using var host = Start("garbage", TimeSpan.FromSeconds(30));
        await host.OpenAsync(_projectId, CancellationToken.None);

        var result = await host.CommandAsync(_projectId, Command("cmd-1"), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AdapterHostState.Running, host.State);
        Assert.Contains(host.RecentOutput(), line => line.Contains("malformed message dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_servers_environment_does_not_reach_the_adapter()
    {
        Environment.SetEnvironmentVariable("BAZAAR_TEST_SECRET", "hunter2");
        try
        {
            await using var host = Start("env", TimeSpan.FromSeconds(30));
            await host.OpenAsync(_projectId, CancellationToken.None);

            var result = await host.CommandAsync(_projectId, Command("cmd-1"), CancellationToken.None);

            Assert.StartsWith("<unset>|", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task Only_variables_the_server_names_are_added_to_the_adapters_environment()
    {
        await using var host = Start("env", TimeSpan.FromSeconds(30), environment: new Dictionary<string, string> { ["BAZAAR_TEST_EXTRA"] = "chosen" });
        await host.OpenAsync(_projectId, CancellationToken.None);

        var result = await host.CommandAsync(_projectId, Command("cmd-1"), CancellationToken.None);

        Assert.EndsWith("|chosen", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_that_never_replies_is_killed_at_the_command_timeout()
    {
        await using var host = Start("unresponsive", TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<EngineAdapterException>(() => host.HealthAsync(CancellationToken.None));

        Assert.Equal(EngineErrorCodes.Timeout, error.Code);
        Assert.Equal(AdapterHostState.Failed, host.State);
    }
}
