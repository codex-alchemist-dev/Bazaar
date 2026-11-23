using System.ComponentModel;
using Bazaar.Contracts.Engines;
using Bazaar.Server.Engines.Abstractions;
using Bazaar.Server.Godot.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Godot.Tests;

public class AdapterTests
{
    private static GodotCommandRequest Check(TempGodotProject p) =>
        new(p.Root, GodotCommandKind.CheckScript, Script: "scripts/main.gd");

    [Fact]
    public async Task Clean_run_succeeds_after_the_canary_fails_as_it_must()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner();
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Reason);
        FakeGodotRunner.RecordedCall call = Assert.Single(runner.NonCanaryCalls);
        Assert.Equal(["--headless", "--check-only", "--script", "scripts/main.gd"], call.Arguments);
        Assert.Equal(GodotPathGuard.RequireProjectRoot(project.Root), call.WorkingDirectory);
        Assert.Equal("godot", call.FileName);
        Assert.Equal(TimeSpan.FromMinutes(15), call.Timeout);
    }

    [Fact]
    public async Task Canary_runs_once_and_its_pass_is_cached()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner();
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await adapter.RunAsync(Check(project), ct);
        await adapter.RunAsync(Check(project), ct);

        Assert.Single(runner.Calls, c => FakeGodotRunner.IsCanary(c.Arguments));
    }

    [Fact]
    public async Task Canary_that_passes_makes_the_adapter_unavailable_and_no_command_runs()
    {
        using var project = new TempGodotProject();
        var runner = new CanaryPassingRunner();
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Unavailable, result.Status);
        Assert.Contains("ignoring", result.Reason);
        Assert.Equal(1, runner.Count);
    }

    [Fact]
    public async Task Canary_is_retried_after_it_failed_to_fail()
    {
        using var project = new TempGodotProject();
        var runner = new CanaryPassingRunner();
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await adapter.RunAsync(Check(project), ct);
        await adapter.RunAsync(Check(project), ct);

        Assert.Equal(2, runner.Count);
    }

    [Fact]
    public async Task Canary_timeout_is_unavailable()
    {
        using var project = new TempGodotProject();
        var runner = new DelegatingRunner((_, _) => Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, TimedOut: true)));
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotAvailability availability = await adapter.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.False(availability.Available);
        Assert.Contains("timed out", availability.Reason);
    }

    [Fact]
    public async Task Canary_with_exit_code_zero_but_error_lines_counts_as_failing()
    {
        var runner = new DelegatingRunner((_, _) => FakeGodotRunner.Ok("SCRIPT ERROR: Parse Error: x\n   at: y (res://canary.gd:3)\n"));
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotAvailability availability = await adapter.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.True(availability.Available);
    }

    [Fact]
    public async Task Missing_binary_is_unavailable_with_a_reason_for_the_canary_and_for_runs()
    {
        using var project = new TempGodotProject();
        var runner = new DelegatingRunner((_, _) => throw new FileNotFoundException("godot not found"));
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Unavailable, result.Status);
        Assert.Contains("Could not start", result.Reason);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task Binary_that_vanishes_after_the_canary_is_unavailable_for_the_run()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => throw new Win32Exception("gone"));
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Non_zero_exit_fails_even_with_no_diagnostics()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Exit(1));
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Failed, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Exit_zero_with_error_lines_fails()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("SCRIPT ERROR: bad\n   at: f (res://scripts/main.gd:4)\n"));
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Failed, result.Status);
        Assert.Equal(0, result.ExitCode);
        GodotDiagnostic d = Assert.Single(result.Diagnostics);
        Assert.Equal(("scripts/main.gd", 4), (d.File, d.Line));
    }

    [Fact]
    public async Task Exit_zero_with_only_warnings_succeeds_and_keeps_them()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok(string.Empty, "WARNING: just a warning\n"));
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Succeeded, result.Status);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task Errors_on_stderr_are_parsed_too()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Exit(255, string.Empty, "ERROR: bad import\n"));
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(new GodotCommandRequest(project.Root, GodotCommandKind.Import), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Failed, result.Status);
        Assert.Equal("bad import", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public async Task Huge_output_is_cut_and_marked()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok(new string('x', 5000)));
        using var adapter = new GodotAdapter(new GodotAdapterOptions { MaxOutputCharacters = 100 }, runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.True(result.OutputTruncated);
        Assert.Equal(GodotRunStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Timeout_from_the_runner_is_reported_and_uses_the_configured_minutes()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "partial", string.Empty, TimedOut: true)));
        using var adapter = new GodotAdapter(new GodotAdapterOptions { CommandTimeoutMinutes = 7 }, runner);

        GodotRunResult result = await adapter.RunAsync(Check(project), TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal(TimeSpan.FromMinutes(7), Assert.Single(runner.NonCanaryCalls).Timeout);
    }

    [Fact]
    public async Task Request_timeout_overrides_and_is_capped_at_120_minutes()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner();
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await adapter.RunAsync(Check(project) with { Timeout = TimeSpan.FromMinutes(3) }, ct);
        await adapter.RunAsync(Check(project) with { Timeout = TimeSpan.FromHours(10) }, ct);

        Assert.Equal([TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(120)], runner.NonCanaryCalls.Select(c => c.Timeout).ToArray());
        await Assert.ThrowsAsync<GodotRequestRejectedException>(() => adapter.RunAsync(Check(project) with { Timeout = TimeSpan.Zero }, ct));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var project = new TempGodotProject();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var runner = new FakeGodotRunner(async (_, _, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new ProcessResult(0, string.Empty, string.Empty, false);
        });
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        Task<GodotRunResult> run = adapter.RunAsync(Check(project), cts.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_before_running()
    {
        using var project = new TempGodotProject();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var runner = new FakeGodotRunner();
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.RunAsync(Check(project), cts.Token));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Rejected_requests_start_no_process_at_all()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner();
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        await Assert.ThrowsAsync<GodotRequestRejectedException>(() =>
            adapter.RunAsync(new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: "../x.gd"), TestContext.Current.CancellationToken));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Runs_in_one_project_are_serialised()
    {
        using var project = new TempGodotProject();
        int running = 0;
        int peak = 0;
        var runner = new FakeGodotRunner(async (_, _, token) =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(40, token);
            Interlocked.Decrement(ref running);
            return new ProcessResult(0, string.Empty, string.Empty, false);
        });
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => adapter.RunAsync(Check(project), ct)));

        Assert.Equal(1, peak);
        Assert.Equal(6, runner.NonCanaryCalls.Count);
    }

    [Fact]
    public async Task Runs_in_different_projects_run_in_parallel()
    {
        using var first = new TempGodotProject();
        using var second = new TempGodotProject();
        var both = new CountdownEvent(2);
        var runner = new FakeGodotRunner(async (_, _, token) =>
        {
            both.Signal();
            // Both runs must be in flight at once for either to finish.
            await Task.Run(() => both.Wait(TimeSpan.FromSeconds(10), token), token);
            return new ProcessResult(0, string.Empty, string.Empty, false);
        });
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);
        CancellationToken ct = TestContext.Current.CancellationToken;

        GodotRunResult[] results = await Task.WhenAll(adapter.RunAsync(Check(first), ct), adapter.RunAsync(Check(second), ct));

        Assert.All(results, r => Assert.Equal(GodotRunStatus.Succeeded, r.Status));
        Assert.Equal(0, both.CurrentCount);
    }

    [Fact]
    public async Task A_queued_run_can_be_cancelled_while_waiting_for_the_project_lock()
    {
        using var project = new TempGodotProject();
        var release = new TaskCompletionSource();
        var firstStarted = new TaskCompletionSource();
        var runner = new FakeGodotRunner(async (_, _, _) =>
        {
            firstStarted.TrySetResult();
            await release.Task;
            return new ProcessResult(0, string.Empty, string.Empty, false);
        });
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Task<GodotRunResult> first = adapter.RunAsync(Check(project), ct);
        await firstStarted.Task.WaitAsync(ct);
        using var cts = new CancellationTokenSource();
        Task<GodotRunResult> queued = adapter.RunAsync(Check(project), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();

        Assert.Equal(GodotRunStatus.Succeeded, (await first).Status);
    }

    [Fact]
    public async Task Export_runs_with_the_validated_arguments()
    {
        using var project = new TempGodotProject();
        project.WritePresets("Linux");
        var runner = new FakeGodotRunner();
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), runner);

        GodotRunResult result = await adapter.RunAsync(
            new GodotCommandRequest(project.Root, GodotCommandKind.Export, Preset: "Linux", OutputDirectory: "build"),
            TestContext.Current.CancellationToken);

        Assert.Equal(GodotRunStatus.Succeeded, result.Status);
        Assert.Equal(["--headless", "--export-release", "Linux", "build"], Assert.Single(runner.NonCanaryCalls).Arguments);
    }

    [Fact]
    public void Canary_resource_is_embedded_and_really_is_broken()
    {
        string script = GodotAdapter.ReadCanary();

        Assert.Contains("extends", script);
        Assert.Contains("bazaar_canary_undefined_symbol(", script);
        Assert.False(script.TrimEnd().EndsWith(')'));
    }

    [Fact]
    public void Manifest_declares_the_godot_capabilities_and_needs_no_gui()
    {
        EngineManifest manifest = GodotAdapterManifest.Create(new GodotAdapterOptions { BuildVariant = GodotBuildVariant.Dotnet, BinaryPath = "/opt/godot" });

        Assert.Equal("godot", manifest.EngineId);
        Assert.Equivalent(
            new[] { EngineCapability.Import, EngineCapability.Export, EngineCapability.Test, EngineCapability.ScriptCheck },
            manifest.Capabilities);
        Assert.False(manifest.NeedsGuiHost);
        Assert.Equal("dotnet", manifest.BuildVariant);
        Assert.Equal("/opt/godot", manifest.BinaryPath);
        Assert.Equal("gdscript", GodotAdapterManifest.Create().BuildVariant);
    }

    [Fact]
    public void Registration_adds_the_adapter_and_keeps_a_host_runner()
    {
        var hostRunner = new FakeGodotRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner>(hostRunner);
        services.AddBazaarGodot(new GodotAdapterOptions { BinaryPath = "godot-x" });
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GodotAdapter>(provider.GetRequiredService<IGodotAdapter>());
        Assert.Same(hostRunner, provider.GetRequiredService<IProcessRunner>());
        Assert.Equal("godot-x", provider.GetRequiredService<GodotAdapterOptions>().BinaryPath);
    }

    [Fact]
    public void Registration_falls_back_to_the_system_runner()
    {
        var services = new ServiceCollection();
        services.AddBazaarGodot();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<SystemGodotProcessRunner>(provider.GetRequiredService<IProcessRunner>());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private sealed class DelegatingRunner(Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> handler) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken) =>
            handler(arguments, cancellationToken);
    }

    /// <summary>A Godot that ignores every flag: it exits 0 with no output, canary included.</summary>
    private sealed class CanaryPassingRunner : IProcessRunner
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return FakeGodotRunner.Ok();
        }
    }
}
