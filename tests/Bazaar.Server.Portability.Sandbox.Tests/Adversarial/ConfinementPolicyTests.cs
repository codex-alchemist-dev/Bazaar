using System.Text;

namespace Bazaar.Server.Portability.Sandbox.Tests.Adversarial;

// Fail-closed policy: an untrusted file is never parsed when a required confinement item is missing.
public class ConfinementPolicyTests
{
    private sealed class FakeConfinement : IParserConfinement
    {
        private readonly string[] _applied;
        private readonly string[] _notApplied;
        private readonly string _script;

        public FakeConfinement(string[] applied, string[] notApplied, string script)
        {
            _applied = applied;
            _notApplied = notApplied;
            _script = script;
        }

        public string Name => "fake";

        public ConfinementPlan Prepare(ConfinementLimits limits, LaunchCommand command) =>
            new(new LaunchCommand("/bin/sh", ["-c", _script]), _applied, _notApplied);
    }

    private sealed class Sink : IParserSignalSink
    {
        public List<ParserSignal> Signals { get; } = [];
        public void Raise(ParserSignal signal) => Signals.Add(signal);
    }

    private static ImportSource Source() => new("a.txt", Encoding.UTF8.GetBytes("x"));

    [Fact]
    public void A_missing_no_network_item_refuses_the_job_before_any_child_starts_and_raises_a_signal()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "uses /bin/sh");
        var canary = Path.Combine(Path.GetTempPath(), "bazaar-started-" + Guid.NewGuid().ToString("N"));
        var sink = new Sink();
        var confinement = new FakeConfinement(["process-group-kill"], ["no-network"], $"touch '{canary}'");
        var options = new ParserSandboxOptions { RequiredConfinement = ["no-network", "process-group-kill"] };
        var runner = new ParserSandboxRunner(options, confinement, sink);

        var error = Assert.Throws<ParserSandboxException>(() => runner.Parse("x", Source()));

        Assert.Equal(ParserFailureKind.ConfinementUnavailable, error.Kind);
        Assert.Contains("no-network", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(canary), "no process may start when a required item is missing");
        var signal = Assert.Single(sink.Signals);
        Assert.Equal(ParserSignals.ConfinementMissing, signal.Name);
        Assert.False(error.CountsAsCrash);
    }

    [Fact]
    public void A_missing_group_kill_item_refuses_the_job_too()
    {
        var confinement = new FakeConfinement(["no-network"], ["process-group-kill"], "exit 0");
        var runner = new ParserSandboxRunner(new ParserSandboxOptions { RequiredConfinement = ["no-network", "process-group-kill"] }, confinement);

        var error = Assert.Throws<ParserSandboxException>(() => runner.Parse("x", Source()));

        Assert.Equal(ParserFailureKind.ConfinementUnavailable, error.Kind);
        Assert.Contains("process-group-kill", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void When_every_required_item_is_applied_the_child_runs()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "uses /bin/sh");
        var done = HostileChild.Send(HostileChild.Frame(HostileChild.DoneZero));
        var confinement = new FakeConfinement(["no-network", "process-group-kill"], [], done);
        var runner = new ParserSandboxRunner(new ParserSandboxOptions { RequiredConfinement = ["no-network", "process-group-kill"] }, confinement);

        Assert.Empty(runner.Parse("x", Source()));
    }

    [Fact]
    public void The_shipped_default_requires_no_network_and_a_group_kill_on_linux_and_tree_kill_on_windows()
    {
        var required = new ParserSandboxOptions().RequiredConfinement;

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("no-network", required);
            Assert.Contains("process-group-kill", required);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.Contains("process-tree-kill", required);
            Assert.Contains("no-child-processes", required);
        }
    }

    [Fact]
    public void The_tree_kill_only_fallback_cannot_run_under_the_default_policy_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the Linux default requires no-network");
        var runner = new ParserSandboxRunner(new ParserSandboxOptions(), new TreeKillOnlyConfinement());

        var error = Assert.Throws<ParserSandboxException>(() => runner.Parse("x", Source()));

        Assert.Equal(ParserFailureKind.ConfinementUnavailable, error.Kind);
    }

    [Fact]
    public void The_real_linux_provider_under_the_default_policy_either_runs_confined_or_refuses_by_name()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux provider");
        var runner = new ParserSandboxRunner(new ParserSandboxOptions { ImporterAssemblies = [typeof(HangImporter).Assembly.Location] });

        try
        {
            var records = runner.Parse("bazaar.import.txt", new ImportSource("n.txt", Encoding.UTF8.GetBytes("hi")));
            Assert.Contains("no-network", runner.LastRun!.Applied);
            Assert.Equal("hi", Assert.Single(records).Body);
        }
        catch (ParserSandboxException ex)
        {
            // A host without a working unshare: refusing is the correct behaviour.
            Assert.Equal(ParserFailureKind.ConfinementUnavailable, ex.Kind);
            Assert.Contains("no-network", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_linux_provider_applies_a_native_memory_limit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/usr/bin/prlimit"), "prlimit");
        var plan = new LinuxParserConfinement().Prepare(new ConfinementLimits(2L * 1024 * 1024 * 1024, 60), new LaunchCommand("dotnet", ["x.dll"]));

        Assert.Contains(LinuxParserConfinement.ItemMemoryLimit, plan.Applied);
        Assert.Contains(plan.Command.Arguments, a => a.StartsWith("--data=", StringComparison.Ordinal));
    }

    [Fact]
    public void Native_memory_past_the_ceiling_fails_in_the_real_child()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/usr/bin/prlimit"), "prlimit");
        var options = new ParserSandboxOptions
        {
            ImporterAssemblies = [typeof(HangImporter).Assembly.Location],
            RequiredConfinement = [],
            MaxMemoryBytes = 256L * 1024 * 1024,
        };
        var runner = new ParserSandboxRunner(options);

        var error = Assert.Throws<ParserSandboxException>(() => runner.Parse("test.native", new ImportSource("n.txt", Encoding.UTF8.GetBytes("x"))));

        Assert.True(error.Kind is ParserFailureKind.LimitExceeded or ParserFailureKind.Crash or ParserFailureKind.ParserError, error.Kind.ToString());
    }

    [Fact]
    public void The_child_environment_is_empty_plus_a_short_allowlist_and_never_carries_server_secrets()
    {
        var server = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            ["DOTNET_ROOT"] = "/dotnet",
            ["SystemRoot"] = "C:\\Windows",
            ["BAZAAR_DB_PASSWORD"] = "canary-secret",
            ["ANTHROPIC_API_KEY"] = "canary-key",
            ["TMPDIR"] = "/server-tmp",
        };

        var env = ParserSandboxRunner.BuildChildEnvironment(new ParserSandboxOptions(), "/scratch", name => server.GetValueOrDefault(name));

        Assert.DoesNotContain(env, kv => kv.Value.Contains("canary", StringComparison.Ordinal));
        Assert.DoesNotContain("PATH", env.Keys);
        Assert.Equal("/scratch", env["TMPDIR"]);
        Assert.All(env.Keys, k => Assert.True(
            k.StartsWith("DOTNET_", StringComparison.Ordinal) || k is "SystemRoot" or "windir" or "TMPDIR" or "TMP" or "TEMP", k));
        Assert.Equal((2L * 1024 * 1024 * 1024).ToString("x", System.Globalization.CultureInfo.InvariantCulture), env["DOTNET_GCHeapHardLimit"]);
    }

    [Fact]
    public void A_canary_in_the_test_process_environment_does_not_reach_the_real_child()
    {
        Environment.SetEnvironmentVariable("BAZAAR_TEST_SECRET", "canary-for-the-child");
        try
        {
            var options = new ParserSandboxOptions { ImporterAssemblies = [typeof(HangImporter).Assembly.Location], RequiredConfinement = [] };
            var runner = new ParserSandboxRunner(options);

            var records = runner.Parse("test.env", new ImportSource("e.txt", Encoding.UTF8.GetBytes("x")));

            Assert.Equal("none", Assert.Single(records).Body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_TEST_SECRET", null);
        }
    }
}
