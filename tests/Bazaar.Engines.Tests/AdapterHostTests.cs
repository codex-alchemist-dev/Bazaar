using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Engines.Adapters;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Engines.Tests;

public class AdapterHostTests
{
    private const string _projectId = "project-a";

    /// <summary>The fake adapter is built next to this test assembly by the FakeAdapter project reference.</summary>
    private static string FakeAdapterPath => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "FakeAdapter.exe" : "FakeAdapter");

    private static AdapterHost StartFake(string mode, TimeSpan commandTimeout, string? slowMilliseconds = null, TimeProvider? clock = null)
    {
        var arguments = slowMilliseconds is null ? new[] { mode } : new[] { mode, slowMilliseconds };
        return AdapterHost.Start(new AdapterProcessOptions(FakeAdapterPath, arguments, commandTimeout, TimeProvider: clock));
    }

    /// <summary>
    /// Waits until the host has read <paramref name="text"/> from the child. The bound is generous and only guards a hung test:
    /// the wait ends as soon as the line arrives, so a slow machine makes the test slower, never wrong.
    /// </summary>
    private static async Task WaitForOutputAsync(AdapterHost host, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (!host.RecentOutput().Any(line => line.Contains(text, StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < deadline, $"The adapter never printed '{text}'.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static EngineCommand Command(string commandId, string name = "echo") =>
        new(commandId, name, JsonSerializer.SerializeToElement(new { target = "scene" }, EngineJson.Options));

    [Fact]
    public async Task Normal_round_trip_opens_runs_a_command_and_closes()
    {
        await using var host = StartFake("normal", TimeSpan.FromSeconds(30));
        var outputs = new List<AdapterOutput>();
        host.OutputReceived += outputs.Add;

        await host.OpenAsync(_projectId, CancellationToken.None);
        var health = await host.HealthAsync(CancellationToken.None);
        var result = await host.CommandAsync(_projectId, Command("cmd-1"), CancellationToken.None);
        await host.CloseAsync(_projectId, CancellationToken.None);

        Assert.Equal("ok", health!.Value.GetProperty("status").GetString());
        Assert.Equal("cmd-1", result.CommandId);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("echo line 1\necho line 2", result.Output);

        // The adapter's own per-project stream counter: 1 then 2, delivered before the command result.
        Assert.Equal([1L, 2L], outputs.Select(output => output.Seq));
        Assert.All(outputs, output => Assert.Equal(_projectId, output.ProjectId));
        Assert.Equal(2, host.LastSeq(_projectId));

        Assert.Equal(AdapterHostState.Closed, host.State);
        Assert.Equal(0, host.ExitCode);
    }

    [Fact]
    public async Task Timeout_kills_the_child_fails_the_session_and_reports_last_output()
    {
        // A fake clock decides when the timeout fires: only after the child has printed its output, so the report is complete.
        var clock = new FakeTimeProvider();
        await using var host = StartFake("hang", TimeSpan.FromSeconds(5), clock: clock);
        await host.OpenAsync(_projectId, CancellationToken.None);

        var pending = host.CommandAsync(_projectId, Command("cmd-hang"), CancellationToken.None);
        await WaitForOutputAsync(host, "step 2");
        clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<EngineAdapterException>(() => pending);

        Assert.Equal(EngineErrorCodes.Timeout, error.Code);
        Assert.Contains("step 2", error.LastOutput);
        Assert.Equal(AdapterHostState.Failed, host.State);
        Assert.NotNull(host.ExitCode);

        // A failed session refuses new work with AdapterUnavailable.
        var later = await Assert.ThrowsAsync<EngineAdapterException>(() => host.HealthAsync(CancellationToken.None));
        Assert.Equal(EngineErrorCodes.AdapterUnavailable, later.Code);
    }

    [Fact]
    public async Task Hanging_health_call_times_out_like_a_command()
    {
        var clock = new FakeTimeProvider();
        await using var host = StartFake("unresponsive", TimeSpan.FromSeconds(5), clock: clock);
        await WaitForOutputAsync(host, "booting");

        var pending = host.HealthAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<EngineAdapterException>(() => pending);

        Assert.Equal(EngineErrorCodes.Timeout, error.Code);
        Assert.Contains("booting", error.LastOutput);
        Assert.Equal(AdapterHostState.Failed, host.State);
        Assert.NotNull(host.ExitCode);
    }

    [Fact]
    public async Task Crash_fails_every_pending_request_with_adapter_crashed()
    {
        await using var host = StartFake("crash", TimeSpan.FromSeconds(30));

        var first = host.CommandAsync(_projectId, Command("cmd-a"), CancellationToken.None);
        var second = host.CommandAsync(_projectId, Command("cmd-b"), CancellationToken.None);

        var firstError = await Assert.ThrowsAsync<EngineAdapterException>(() => first);
        var secondError = await Assert.ThrowsAsync<EngineAdapterException>(() => second);

        Assert.Equal(EngineErrorCodes.AdapterCrashed, firstError.Code);
        Assert.Equal(EngineErrorCodes.AdapterCrashed, secondError.Code);
        Assert.Contains("about to fail", firstError.LastOutput);
        Assert.Equal(3, host.ExitCode);
        Assert.Equal(AdapterHostState.Failed, host.State);
    }

    [Fact]
    public async Task Cancel_stops_waiting_and_leaves_the_child_running()
    {
        await using var host = StartFake("hang", TimeSpan.FromSeconds(30));
        await host.OpenAsync(_projectId, CancellationToken.None);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.CommandAsync(_projectId, Command("cmd-cancel"), cancellation.Token));

        // The adapter acknowledges an explicit cancel, and the session stays usable.
        var acknowledged = await host.CancelAsync(_projectId, "cmd-cancel", CancellationToken.None);
        Assert.True(acknowledged!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(AdapterHostState.Running, host.State);

        var health = await host.HealthAsync(CancellationToken.None);
        Assert.Equal("ok", health!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Slow_reply_within_the_timeout_succeeds()
    {
        await using var host = StartFake("slow", TimeSpan.FromSeconds(10), slowMilliseconds: "300");
        await host.OpenAsync(_projectId, CancellationToken.None);

        var result = await host.CommandAsync(_projectId, Command("cmd-slow"), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AdapterHostState.Running, host.State);
    }

    [Fact]
    public void Start_rejects_a_non_positive_command_timeout()
    {
        var options = new AdapterProcessOptions(FakeAdapterPath, ["normal"], TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => AdapterHost.Start(options));
    }
}
