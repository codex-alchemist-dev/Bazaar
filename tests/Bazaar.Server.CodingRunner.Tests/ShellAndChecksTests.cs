using System.Text.Json;
using Bazaar.Contracts.Coding;
using Bazaar.Server.Coding.Checks;
using Bazaar.Server.Coding.Workspace;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingRunner.Handlers;
using Bazaar.Server.CodingStore;

namespace Bazaar.Server.CodingRunner.Tests;

/// <summary>Tests for the <c>shell</c> and <c>run_checks</c> handlers (doc 17, "Shell and run_checks handlers").</summary>
public sealed class ShellAndChecksTests
{
    private const string _root = "/work/agent-1/wt";
    private const string _wt = "wt-1";

    private sealed class Guests(IGuestChannel channel, bool known = true) : IGuestWorktreeSource
    {
        public ValueTask<GuestWorktreeTarget?> ResolveAsync(string agentId, string worktreeKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult(known && worktreeKey == _wt ? new GuestWorktreeTarget(channel, _root) : null);
    }

    private sealed class Subjects(CheckSubject? subject) : ICheckSubjectSource
    {
        public ValueTask<CheckSubject?> ResolveAsync(string agentId, string worktreeKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult(subject);
    }

    private sealed class Store(bool fail = false) : ICheckRunStore
    {
        public List<CheckRunRecord> Rows { get; } = [];

        public ValueTask AppendAsync(CheckRunRecord record, CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new InvalidOperationException("db down");
            }

            Rows.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<CheckRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken) => ValueTask.FromResult(Rows.FirstOrDefault(r => r.Id == id));

        public ValueTask<IReadOnlyList<CheckRunRecord>> ListByCommitAsync(string commit, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CheckRunRecord>>([.. Rows.Where(r => r.Commit == commit)]);
    }

    private sealed class ThrowingChannel : IGuestChannel
    {
        public Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct) =>
            throw new IOException("secret-host-detail");

        public Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct) => throw new NotSupportedException();

        public Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct) => throw new NotSupportedException();
    }

    private static CodingToolCall Call(string tool, string json = "{}", string? worktree = _wt) =>
        new("agent-1", "session-1", tool, JsonElement.Parse(json), Role: ToolSessionRole.Actor, Taint: SessionTaint.None, WorktreeKey: worktree);

    private static JsonElement Root(CodingToolOutput o) => JsonElement.Parse(o.Output);

    private static readonly string[] _ls = ["ls"];
    private static readonly string[] _build = ["dotnet", "build"];
    private static readonly string[] _test = ["dotnet", "test"];

    private const string _vsSummary = "Passed!  - Failed:     0, Passed:    12, Skipped:     1, Total:    13, Duration: 3 s\n";

    private static CheckSubject Subject(IReadOnlyList<ChangedFile>? files = null, int? baseCount = 13, params CheckCommand[] commands) =>
        new("abc1234", "base123", baseCount, 0,
            commands.Length > 0 ? commands : [new CheckCommand("build", CheckToolKind.Build, _build), new CheckCommand("test", CheckToolKind.Test, _test)],
            files ?? []);

    private static InMemoryGuestChannel PassingChannel()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_build, 0, ("stdout", "Build succeeded.\n"));
        channel.ScriptExit(_test, 0, ("stdout", _vsSummary));
        return channel;
    }

    private static CodingToolRunner RunnerFor(ICodingToolHandler handler, PermissionOutcome outcome, out InMemoryToolCallTrace trace)
    {
        trace = new InMemoryToolCallTrace();
        var gate = new ToolPermissionGate(
            new FakeGenericToolPolicy(_ => new GenericPolicyDecision(outcome, "test policy")),
            trace,
            new InMemoryAgentFreeze(),
            new CodingPermissionOptions());
        return new CodingToolRunner([handler], new ToolCallOutcomeRecorder(gate, trace), new WorktreeLocks());
    }

    // ---- shell -------------------------------------------------------------------------------------------------------

    private static ShellToolHandler Shell(IGuestChannel channel, ShellToolOptions? options = null) => new(new Guests(channel), options);

    [Fact]
    public async Task Shell_runs_in_the_guest_worktree_and_returns_a_structured_untrusted_result()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(["ls", "-la"], 0, ("stdout", "a.txt\n"), ("stderr", "warn\n"));

        var output = await Shell(channel).ExecuteAsync(Call("shell", """{"argv":["ls","-la"],"cwd":"src/./app"}"""), TestContext.Current.CancellationToken);

        Assert.Equal(0, output.Outcome.ExitCode);
        var json = Root(output);
        Assert.Equal(0, json.GetProperty("exit_code").GetInt32());
        Assert.Equal("a.txt\n", json.GetProperty("stdout").GetString());
        Assert.Equal("warn\n", json.GetProperty("stderr").GetString());
        Assert.True(json.GetProperty("output_is_untrusted").GetBoolean());
        var request = Assert.Single(channel.Requests);
        Assert.Equal(_root + "/src/app", request.Cwd);
        Assert.Empty(request.Env);
        Assert.Equal(["ls", "-la"], request.Argv);
    }

    [Fact]
    public async Task Shell_nonzero_exit_is_reported_not_hidden()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(["false"], 3);

        var output = await Shell(channel).ExecuteAsync(Call("shell", """{"argv":["false"]}"""), TestContext.Current.CancellationToken);

        Assert.Equal(3, output.Outcome.ExitCode);
    }

    [Fact]
    public async Task Shell_timeout_is_never_a_pass_even_with_exit_zero()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptTimeout(["sleep", "99"], 0);

        var output = await Shell(channel).ExecuteAsync(Call("shell", """{"argv":["sleep","99"],"timeout_seconds":1}"""), TestContext.Current.CancellationToken);

        Assert.True(output.Outcome.TimedOut);
        Assert.NotEqual(0, output.Outcome.ExitCode);
        Assert.True(Root(output).GetProperty("timed_out").GetBoolean());
    }

    [Fact]
    public async Task Shell_host_side_timeout_stops_a_hung_guest_and_keeps_partial_output()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptHang(["hang"], ("stdout", "partial\n"));
        var options = new ShellToolOptions { DefaultTimeout = TimeSpan.FromMilliseconds(100), MaxTimeout = TimeSpan.FromMinutes(1) };

        var output = await Shell(channel, options).ExecuteAsync(Call("shell", """{"argv":["hang"]}"""), TestContext.Current.CancellationToken);

        Assert.True(output.Outcome.TimedOut);
        Assert.Equal("partial\n", Root(output).GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task Shell_caps_output_and_says_so()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(["big"], 0, ("stdout", new string('x', 5_000)));
        var options = new ShellToolOptions { OutputCap = 1_024 };

        var output = await Shell(channel, options).ExecuteAsync(Call("shell", """{"argv":["big"]}"""), TestContext.Current.CancellationToken);

        var json = Root(output);
        Assert.Equal(1_024, json.GetProperty("stdout").GetString()!.Length);
        Assert.True(json.GetProperty("output_truncated").GetBoolean());
    }

    [Fact]
    public async Task Shell_redacts_secret_shapes_from_output()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(["env"], 0, ("stdout", "ghp_abcdefghijklmnopqrstuvwxyz0123456789\nAPI_KEY=hunter2hunter2\n-----BEGIN RSA PRIVATE KEY-----\nMIIE\n-----END RSA PRIVATE KEY-----\n"));

        var output = await Shell(channel).ExecuteAsync(Call("shell", """{"argv":["env"]}"""), TestContext.Current.CancellationToken);

        var stdout = Root(output).GetProperty("stdout").GetString()!;
        Assert.DoesNotContain("ghp_", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIE", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"argv":["echo","hi; rm -rf /"]}""")]
    [InlineData("""{"argv":["echo","$(whoami)","`id`","a|b","a&&b",">x"]}""")]
    public async Task Shell_metacharacters_stay_literal_argv_data(string json)
    {
        var channel = new InMemoryGuestChannel();
        var args = JsonElement.Parse(json).GetProperty("argv").EnumerateArray().Select(e => e.GetString()!).ToList();
        channel.ScriptExit(args, 0);

        var output = await Shell(channel).ExecuteAsync(Call("shell", json), TestContext.Current.CancellationToken);

        Assert.Equal(0, output.Outcome.ExitCode);
        Assert.Equal(args, Assert.Single(channel.Requests).Argv);
    }

    [Theory]
    [InlineData("""{"command":"ls; id"}""")]
    [InlineData("""{"argv":"ls"}""")]
    [InlineData("""{"argv":[]}""")]
    [InlineData("""{"argv":["ls",""]}""")]
    [InlineData("""{"argv":["ls\u0000id"]}""")]
    [InlineData("""{"argv":["ls"],"env":{"A":"b"}}""")]
    [InlineData("""{"argv":["ls"],"timeout_seconds":0}""")]
    [InlineData("""{"argv":["ls"],"timeout_seconds":99999}""")]
    public async Task Shell_malformed_arguments_run_nothing(string json)
    {
        var channel = new InMemoryGuestChannel();

        var output = await Shell(channel).ExecuteAsync(Call("shell", json), TestContext.Current.CancellationToken);

        Assert.Equal(64, output.Outcome.ExitCode);
        Assert.Empty(channel.Requests);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("/etc")]
    [InlineData("a/../../b")]
    [InlineData("C:\\Windows")]
    public async Task Shell_path_jail_refuses_cwd_that_leaves_the_worktree(string cwd)
    {
        var channel = new InMemoryGuestChannel();
        var json = JsonSerializer.Serialize(new { argv = _ls, cwd });

        var output = await Shell(channel).ExecuteAsync(Call("shell", json), TestContext.Current.CancellationToken);

        Assert.Equal(64, output.Outcome.ExitCode);
        Assert.Equal("path_escape", Root(output).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public async Task Shell_without_a_held_worktree_is_refused()
    {
        var channel = new InMemoryGuestChannel();

        var unknown = await new ShellToolHandler(new Guests(channel, known: false)).ExecuteAsync(Call("shell", """{"argv":["ls"]}"""), TestContext.Current.CancellationToken);
        var none = await Shell(channel).ExecuteAsync(Call("shell", """{"argv":["ls"]}""", worktree: null), TestContext.Current.CancellationToken);

        Assert.Equal(64, unknown.Outcome.ExitCode);
        Assert.Equal(64, none.Outcome.ExitCode);
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public async Task Shell_cancellation_stops_the_guest_and_is_rethrown()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptHang(["hang"]);
        using var cts = new CancellationTokenSource();
        var task = Shell(channel).ExecuteAsync(Call("shell", """{"argv":["hang"]}"""), cts.Token);
        while (channel.ActiveExecutions == 0)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, channel.ActiveExecutions);
    }

    [Fact]
    public async Task Shell_guest_fault_is_an_error_with_no_detail()
    {
        var output = await Shell(new ThrowingChannel()).ExecuteAsync(Call("shell", """{"argv":["ls"]}"""), TestContext.Current.CancellationToken);

        Assert.Equal(1, output.Outcome.ExitCode);
        Assert.Equal("guest_unavailable", Root(output).GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("secret-host-detail", output.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shell_denied_gate_never_reaches_the_guest()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(["ls"], 0);
        var runner = RunnerFor(Shell(channel), PermissionOutcome.Deny, out _);

        var result = await runner.RunAsync(Call("shell", """{"argv":["ls"]}"""), TestContext.Current.CancellationToken);

        Assert.Equal(CodingToolStatus.Denied, result.Status);
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public async Task Shell_cancellation_through_the_runner_is_recorded_and_not_a_success()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptHang(["hang"]);
        var runner = RunnerFor(Shell(channel), PermissionOutcome.Allow, out var trace);
        using var cts = new CancellationTokenSource();
        var task = runner.RunAsync(Call("shell", """{"argv":["hang"]}"""), cts.Token).AsTask();
        while (channel.ActiveExecutions == 0)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var row = trace.Rows.Single(r => r.Tool == "shell");
        Assert.NotEqual(ToolCallStatus.Succeeded, row.Status);
        Assert.NotEqual(ToolCallStatus.Unknown, row.Status);
    }

    // ---- run_checks --------------------------------------------------------------------------------------------------

    private static RunChecksToolHandler Checks(IGuestChannel channel, CheckSubject? subject, Store store, RunChecksToolOptions? options = null) =>
        new(new Subjects(subject), new Guests(channel), store, options);

    [Fact]
    public async Task Checks_pass_records_a_pass_with_per_tool_exit_codes_and_counts()
    {
        var store = new Store();

        var output = await Checks(PassingChannel(), Subject(), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        Assert.Equal(0, output.Outcome.ExitCode);
        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Pass, row.Verdict);
        Assert.Equal(0, row.BuildExitCode);
        Assert.Equal(12, row.TestsPassed);
        Assert.Equal(1, row.TestsSkipped);
        Assert.Equal(0, row.ToolExitCodes!["build"]);
        Assert.Equal(0, row.ToolExitCodes["test"]);
        Assert.Equal("pass", Root(output).GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Checks_arguments_are_a_closed_empty_schema()
    {
        var store = new Store();

        var output = await Checks(PassingChannel(), Subject(), store).ExecuteAsync(Call("run_checks", """{"commands":[["rm","-rf","/"]]}"""), TestContext.Current.CancellationToken);

        Assert.Equal(64, output.Outcome.ExitCode);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Checks_build_failure_is_fail_and_skips_the_tests()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_build, 1, ("stdout", "error CS1002"));
        var store = new Store();

        var output = await Checks(channel, Subject(), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        Assert.Equal(1, output.Outcome.ExitCode);
        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Fail, row.Verdict);
        Assert.Equal(1, row.BuildExitCode);
        Assert.Null(row.ToolExitCodes!["test"]);
        Assert.Single(channel.Requests);
    }

    [Fact]
    public async Task Checks_failing_test_tool_is_fail()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_build, 0);
        channel.ScriptExit(_test, 1, ("stdout", "Failed!  - Failed:     2, Passed:    10, Skipped:     1, Total:    13\n"));
        var store = new Store();

        await Checks(channel, Subject(), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Fail, row.Verdict);
        Assert.Equal(2, row.TestsFailed);
    }

    [Fact]
    public async Task Checks_timeout_is_error_never_pass()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_build, 0);
        channel.ScriptTimeout(_test, 0);
        var store = new Store();

        var output = await Checks(channel, Subject(), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Error, row.Verdict);
        Assert.Null(row.ToolExitCodes!["test"]);
        Assert.True(output.Outcome.TimedOut);
        Assert.NotEqual(0, output.Outcome.ExitCode);
    }

    [Fact]
    public async Task Checks_guest_fault_is_error()
    {
        var store = new Store();

        await Checks(new ThrowingChannel(), Subject(), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        Assert.Equal(CheckVerdict.Error, Assert.Single(store.Rows).Verdict);
    }

    [Fact]
    public async Task Checks_fake_success_flags_fail_the_run_and_are_recorded()
    {
        var files = new List<ChangedFile>
        {
            new("tests/FooTests.cs", ChangeKind.Modified, "[Fact]\npublic void A() { Assert.True(true); }\n", "[Fact]\n[Ignore(\"later\")]\npublic void A() { }\n"),
        };
        var store = new Store();

        var output = await Checks(PassingChannel(), Subject(files), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Fail, row.Verdict);
        Assert.Contains(CheckFlag.SkipAdded, row.Flags);
        Assert.Contains(CheckFlag.AssertionRemoved, row.Flags);
        Assert.Contains(CheckFlag.TestFileChanged, row.Flags);
        Assert.Equal(1, output.Outcome.ExitCode);
    }

    [Fact]
    public async Task Checks_test_file_change_alone_is_flagged_but_does_not_fail()
    {
        var files = new List<ChangedFile> { new("tests/FooTests.cs", ChangeKind.Modified, "[Fact] void A() { Assert.True(x); }", "[Fact] void A() { Assert.True(x); }\n// c") };
        var store = new Store();

        await Checks(PassingChannel(), Subject(files), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Pass, row.Verdict);
        Assert.Equal([CheckFlag.TestFileChanged], row.Flags);
    }

    [Fact]
    public async Task Checks_test_count_drop_is_fail()
    {
        var store = new Store();

        await Checks(PassingChannel(), Subject(baseCount: 20), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Fail, row.Verdict);
        Assert.Contains(CheckFlag.TestCountDropped, row.Flags);
    }

    [Fact]
    public async Task Checks_unreadable_test_counts_with_a_base_count_are_error()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_build, 0);
        channel.ScriptExit(_test, 0, ("stdout", "all good, trust me\n"));
        var store = new Store();

        await Checks(channel, Subject(), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        Assert.Equal(CheckVerdict.Error, Assert.Single(store.Rows).Verdict);
    }

    [Fact]
    public async Task Checks_reads_the_testing_platform_summary_block()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_build, 0);
        channel.ScriptExit(_test, 0, ("stdout", "Test run summary: Passed!\n  total: 13\n  failed: 0\n  succeeded: 12\n  skipped: 1\n"));
        var store = new Store();

        await Checks(channel, Subject(), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Pass, row.Verdict);
        Assert.Equal(12, row.TestsPassed);
    }

    [Fact]
    public async Task Checks_counts_that_do_not_add_up_are_unreadable()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_build, 0);
        channel.ScriptExit(_test, 0, ("stdout", "Passed!  - Failed:     0, Passed:    99, Skipped:     0, Total:    13\n"));
        var store = new Store();

        await Checks(channel, Subject(), store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        Assert.Equal(CheckVerdict.Error, Assert.Single(store.Rows).Verdict);
    }

    [Fact]
    public async Task Checks_store_failure_claims_no_verdict()
    {
        var output = await Checks(PassingChannel(), Subject(), new Store(fail: true)).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        Assert.Equal(1, output.Outcome.ExitCode);
        Assert.Equal("check_run_not_recorded", Root(output).GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("\"verdict\"", output.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checks_cancellation_appends_an_error_row_and_rethrows()
    {
        var channel = new InMemoryGuestChannel();
        channel.ScriptExit(_build, 0);
        channel.ScriptHang(_test);
        var store = new Store();
        using var cts = new CancellationTokenSource();
        var task = Checks(channel, Subject(), store).ExecuteAsync(Call("run_checks"), cts.Token);
        while (channel.Requests.Count < 2)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var row = Assert.Single(store.Rows);
        Assert.Equal(CheckVerdict.Error, row.Verdict);
        Assert.Equal(0, row.ToolExitCodes!["build"]);
        Assert.Null(row.ToolExitCodes["test"]);
    }

    [Fact]
    public async Task Checks_without_a_valid_configuration_run_nothing()
    {
        var channel = PassingChannel();
        var store = new Store();
        var noBuild = Subject(commands: new CheckCommand("test", CheckToolKind.Test, _test));
        var empty = new CheckSubject("c", null, null, 0, [], []);

        var a = await Checks(channel, null, store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);
        var b = await Checks(channel, noBuild, store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);
        var c = await Checks(channel, empty, store).ExecuteAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        Assert.All(new[] { a, b, c }, o => Assert.Equal(1, o.Outcome.ExitCode));
        Assert.Empty(channel.Requests);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Checks_denied_gate_never_runs()
    {
        var channel = PassingChannel();
        var store = new Store();
        var runner = RunnerFor(Checks(channel, Subject(), store), PermissionOutcome.Deny, out _);

        var result = await runner.RunAsync(Call("run_checks"), TestContext.Current.CancellationToken);

        Assert.Equal(CodingToolStatus.Denied, result.Status);
        Assert.Empty(channel.Requests);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public void Handlers_declare_the_documented_capability_classes()
    {
        var shell = Shell(new InMemoryGuestChannel());
        var checks = Checks(new InMemoryGuestChannel(), Subject(), new Store());

        Assert.Equal(("shell", ToolCapabilityClass.Exec), (shell.Name, shell.CapabilityClass));
        Assert.Equal(("run_checks", ToolCapabilityClass.Read), (checks.Name, checks.CapabilityClass));
        Assert.True(shell.WritesWorktree);
        Assert.True(checks.WritesWorktree);
    }
}
