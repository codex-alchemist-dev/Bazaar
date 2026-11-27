using Cdax.Core;
using Bazaar.Server.Playtest.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Playtest.Tests;

public sealed class PlaytestRunnerTests
{
    private static PlaytestScenario Scenario(params PlaytestAssertion[] assertions) =>
        new(PlaytestEngine.Godot, "build-1", "menu-flow", assertions, TimeSpan.FromSeconds(5));

    private static StateDump Dump(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => v.Value));

    private sealed class Rig
    {
        public FakeHeadlessHost Host { get; } = new();
        public FakeProjectHasher Hasher { get; } = new();
        public RecordingNoteWriter Notes { get; } = new();
        public InMemoryPlaytestRunStore Store { get; } = new();

        public PlaytestRunner Runner(PlaytestSettings? settings = null) =>
            new(Host, Hasher, Notes, Store, settings ?? new PlaytestSettings());
    }

    [Fact]
    public async Task Unchanged_project_hash_with_passing_assertions_passes()
    {
        var rig = new Rig();
        rig.Host.Dump = Dump(("score", "10"));
        var result = await rig.Runner().RunAsync(Scenario(new PlaytestAssertion("a1", "score", "10")), CancellationToken.None);

        Assert.Equal(PlaytestStatus.Passed, result.Status);
        Assert.Equal(PlaytestMode.TierA, result.Mode);
        Assert.Equal(0, PlaytestResult.ReputationWeight);
        Assert.Equal(result.ProjectHashBefore, result.ProjectHashAfter);
        Assert.NotNull(await rig.Store.GetAsync(result.RunId, CancellationToken.None));
        Assert.Single(rig.Notes.Requests);
    }

    [Fact]
    public async Task Changed_project_hash_fails_the_run_even_when_assertions_pass()
    {
        var rig = new Rig();
        rig.Host.Dump = Dump(("score", "10"));
        rig.Hasher.Hashes.Enqueue("hash-a");
        rig.Hasher.Hashes.Enqueue("hash-b");
        var result = await rig.Runner().RunAsync(Scenario(new PlaytestAssertion("a1", "score", "10")), CancellationToken.None);

        Assert.Equal(PlaytestStatus.ProjectChanged, result.Status);
        Assert.Equal("hash-a", result.ProjectHashBefore);
        Assert.Equal("hash-b", result.ProjectHashAfter);
    }

    [Fact]
    public async Task Failing_assertion_is_reported_with_the_actual_value()
    {
        var rig = new Rig();
        rig.Host.Dump = Dump(("score", "7"));
        var result = await rig.Runner().RunAsync(
            Scenario(new PlaytestAssertion("a1", "score", "10"), new PlaytestAssertion("a2", "missing", "x")), CancellationToken.None);

        Assert.Equal(PlaytestStatus.AssertionsFailed, result.Status);
        Assert.Equal(new AssertionResult("a1", false, "7"), result.Assertions[0]);
        Assert.Equal(new AssertionResult("a2", false, null), result.Assertions[1]);
    }

    [Fact]
    public void State_dump_hash_is_deterministic_and_ignores_key_order()
    {
        var one = Dump(("a", "1"), ("b", "2"));
        var two = Dump(("b", "2"), ("a", "1"));
        Assert.Equal(one.ContentHash(), two.ContentHash());
        Assert.Matches("^[0-9a-f]{64}$", one.ContentHash());
        Assert.NotEqual(one.ContentHash(), Dump(("a", "1"), ("b", "3")).ContentHash());
        Assert.NotEqual(Dump(("a", "1b=2")).ContentHash(), one.ContentHash());
    }

    [Fact]
    public async Task Run_records_the_dump_hash()
    {
        var rig = new Rig();
        rig.Host.Dump = Dump(("score", "10"));
        var result = await rig.Runner().RunAsync(Scenario(), CancellationToken.None);
        Assert.Equal(rig.Host.Dump.ContentHash(), result.StateDumpHash);
    }

    [Fact]
    public async Task Hosted_vision_is_off_by_default_and_reaches_the_note_writer_as_off()
    {
        Assert.False(new PlaytestSettings().HostedVision);
        Assert.Equal(60, new PlaytestSettings().CaptureIntervalSeconds);

        var rig = new Rig();
        rig.Host.Dump = Dump(("score", "10"));
        await rig.Runner().RunAsync(Scenario(), CancellationToken.None);
        Assert.False(rig.Notes.Requests.Single().HostedVisionAllowed);

        await rig.Runner(new PlaytestSettings { HostedVision = true }).RunAsync(Scenario(), CancellationToken.None);
        Assert.True(rig.Notes.Requests[^1].HostedVisionAllowed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3601)]
    public void Capture_interval_outside_range_is_rejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaytestSettings { CaptureIntervalSeconds = seconds }.Validated());

    [Fact]
    public async Task Host_that_hangs_times_out_and_the_run_is_recorded()
    {
        var rig = new Rig { Host = { Hang = true } };
        var scenario = Scenario() with { Timeout = TimeSpan.FromSeconds(1) };
        var result = await rig.Runner().RunAsync(scenario, CancellationToken.None);

        Assert.Equal(PlaytestStatus.TimedOut, result.Status);
        Assert.Null(result.StateDumpHash);
        Assert.Empty(rig.Notes.Requests);
        Assert.NotNull(await rig.Store.GetAsync(result.RunId, CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        var rig = new Rig { Host = { Hang = true } };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Runner().RunAsync(Scenario(), cts.Token));
    }

    [Fact]
    public async Task Host_exception_marks_host_failed()
    {
        var rig = new Rig { Host = { Throw = true } };
        var result = await rig.Runner().RunAsync(Scenario(), CancellationToken.None);
        Assert.Equal(PlaytestStatus.HostFailed, result.Status);
    }

    [Fact]
    public async Task Host_without_no_secrets_and_denied_egress_is_rejected_before_running()
    {
        var rig = new Rig { Host = { Profile = new HostProfile(NoSecrets: true, EgressDenied: false) } };
        var result = await rig.Runner().RunAsync(Scenario(), CancellationToken.None);

        Assert.Equal(PlaytestStatus.ProfileRejected, result.Status);
        Assert.Equal(0, rig.Host.Runs);
        Assert.Equal(0, rig.Hasher.Calls);
    }

    [Fact]
    public void Component_manifest_uses_the_playtest_migration_namespace_and_registers_the_runner()
    {
        var component = new PlaytestComponent();
        Assert.Equal("playtest", component.Manifest.Id);
        Assert.Equal(PlaytestComponent.MigrationProvider, component.Manifest.MigrationNamespace);

        var services = new ServiceCollection();
        services.AddSingleton<IEngineHeadlessHost>(new FakeHeadlessHost());
        services.AddSingleton<IProjectHasher>(new FakeProjectHasher());
        services.AddSingleton<IPlaytestNoteWriter>(new RecordingNoteWriter());
        component.AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<PlaytestRunner>(provider.GetRequiredService<IPlaytestRunner>());
        Assert.False(provider.GetRequiredService<PlaytestSettings>().HostedVision);
    }

    [Fact]
    public void Migration_is_embedded_in_the_playtest_namespace()
    {
        var names = typeof(PlaytestComponent).Assembly.GetManifestResourceNames();
        Assert.Contains(names, n => n.Contains("playtest", StringComparison.Ordinal) && n.EndsWith("0001_playtest_run.sql", StringComparison.Ordinal));
    }
}

internal sealed class FakeHeadlessHost : IEngineHeadlessHost
{
    public HostProfile Profile { get; set; } = HostProfile.Required;

    public StateDump Dump { get; set; } = new(new Dictionary<string, string>());

    public bool Hang { get; set; }

    public bool Throw { get; set; }

    public int Runs { get; private set; }

    public HostProfile RequiredProfile => Profile;

    public async Task<StateDump> RunAsync(PlaytestScenario scenario, CancellationToken cancellationToken)
    {
        Runs++;
        if (Throw)
        {
            throw new InvalidOperationException("engine crashed");
        }

        if (Hang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return Dump;
    }
}

internal sealed class FakeProjectHasher : IProjectHasher
{
    public Queue<string> Hashes { get; } = new();

    public int Calls { get; private set; }

    public Task<string> HashAsync(string buildRef, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Hashes.Count > 0 ? Hashes.Dequeue() : "stable-hash");
    }
}

internal sealed class RecordingNoteWriter : IPlaytestNoteWriter
{
    public List<PlaytestNoteRequest> Requests { get; } = [];

    public Task WriteNotesAsync(PlaytestNoteRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.CompletedTask;
    }
}
