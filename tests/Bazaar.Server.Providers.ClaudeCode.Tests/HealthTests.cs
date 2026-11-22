using Bazaar.Server.Modules.Health;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bazaar.Server.Providers.ClaudeCode.Tests;

public sealed class HealthTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bazaar-health-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 19, 0, 0, TimeSpan.Zero));
    private readonly string _binary;

    public HealthTests()
    {
        _binary = Path.Combine(_dir, "claude");
        File.WriteAllText(_binary, "x");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Run(string[] lines, bool hang = false) : IClaudeCodeRun
    {
        public int? ExitCode => 0;

        public string StderrTail => "";

        public async IAsyncEnumerable<string> ReadLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            foreach (var l in lines)
            {
                yield return l;
            }
        }

        public void KillTree()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Runner(Func<IClaudeCodeRun> make) : IClaudeCodeProcessRunner
    {
        public ClaudeCodeInvocation? Last { get; private set; }

        public Task<IClaudeCodeRun> StartAsync(ClaudeCodeInvocation invocation, CancellationToken ct)
        {
            Last = invocation;
            return Task.FromResult(make());
        }
    }

    private ClaudeCodeHealth Health(Runner runner, ClaudeCodeLaneState? lane = null, string? binary = null) =>
        new(new ClaudeCodeOptions { BinaryPath = binary ?? _binary }, lane ?? new ClaudeCodeLaneState(_time), runner, _time);

    [Fact]
    public async Task Self_test_runs_only_version_and_reports_it()
    {
        var runner = new Runner(() => new Run(["2.1.300 (Claude Code)"]));
        var check = await Health(runner).SelfTestAsync(ModuleSelfTestScope.Quick, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Healthy, check.Status);
        Assert.Contains("2.1.300", check.Reason, StringComparison.Ordinal);
        Assert.Equal(["--version"], runner.Last!.Arguments);
        Assert.DoesNotContain(runner.Last.Environment.Keys, k => k.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Self_test_fails_when_the_program_is_missing_or_prints_nothing()
    {
        var missing = await Health(new Runner(() => new Run([])), binary: Path.Combine(_dir, "nope"))
            .SelfTestAsync(ModuleSelfTestScope.Quick, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Failing, missing.Status);

        var silent = await Health(new Runner(() => new Run([])))
            .SelfTestAsync(ModuleSelfTestScope.Quick, TestContext.Current.CancellationToken);
        Assert.Equal(ModuleHealthStatus.Failing, silent.Status);
    }

    [Fact]
    public async Task Self_test_strips_control_characters_and_caps_length()
    {
        var runner = new Runner(() => new Run(["v1\u001b[31m" + new string('x', 200)]));
        var check = await Health(runner).SelfTestAsync(ModuleSelfTestScope.Quick, TestContext.Current.CancellationToken);
        Assert.DoesNotContain('\u001b', check.Reason);
        Assert.True(check.Reason.Length < 120);
    }

    [Fact]
    public async Task Readiness_follows_the_lane_state()
    {
        var lane = new ClaudeCodeLaneState(_time);
        var health = Health(new Runner(() => new Run([])), lane);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(ModuleHealthStatus.Healthy, (await health.ReadinessAsync(ct)).Status);

        lane.MarkRateLimited(TimeSpan.FromMinutes(30));
        var limited = await health.ReadinessAsync(ct);
        Assert.Equal(ModuleHealthStatus.Degraded, limited.Status);
        Assert.Contains("rate limited until", limited.Reason, StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ModuleHealthStatus.Healthy, (await health.ReadinessAsync(ct)).Status);

        lane.MarkDown("signed out", "authentication_failed");
        var down = await health.ReadinessAsync(ct);
        Assert.Equal(ModuleHealthStatus.Failing, down.Status);
        Assert.Contains("signed out", down.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Liveness_does_no_io_and_has_no_dependencies()
    {
        var runner = new Runner(() => throw new InvalidOperationException("no I/O allowed"));
        var health = Health(runner);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ModuleHealthStatus.Healthy, (await health.LivenessAsync(ct)).Status);
        Assert.Empty(await health.DependenciesAsync(ct));
        Assert.Null(runner.Last);
    }
}
