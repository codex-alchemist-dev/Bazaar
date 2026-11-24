using System.Diagnostics;

namespace Bazaar.ModulePermissions.Process.Tests;

public sealed class ModuleProcessRunnerTests : IDisposable
{
    private const string _module = "bazaar.media";
    private const string _shell = "tools/shell";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "bazaar-proc-" + Guid.NewGuid().ToString("N"));

    public ModuleProcessRunnerTests() => Directory.CreateDirectory(_work);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ShellPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
        : "/bin/sh";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ListSink : IModulePermissionDenialSink
    {
        public List<ModulePermissionDenial> Denials { get; } = [];

        public void Record(ModulePermissionDenial denial) => Denials.Add(denial);
    }

    /// <summary>Arguments that make the test shell run one command line.</summary>
    private static string[] Script(string unix, string windows) => OperatingSystem.IsWindows() ? ["/d", "/c", windows] : ["-c", unix];

    private (ModuleProcessRunner Runner, ListSink Sink) Make(
        string[] permissions,
        ModuleProcessOptions? options = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? executable = null)
    {
        var sink = new ListSink();
        var gate = new ModulePermissionGate(sink, TimeProvider.System);
        gate.Activate(_module, permissions);
        var catalog = new ModuleProgramCatalog(new Dictionary<string, ModuleProgram>
        {
            [_shell] = new(executable ?? ShellPath, _work, environment),
        });
        return (new ModuleProcessRunner(gate, _module, catalog, options), sink);
    }

    private ModuleProcessRunner Granted(ModuleProcessOptions? options = null, IReadOnlyDictionary<string, string>? environment = null) =>
        Make(["process:" + _shell], options, environment).Runner;

    [Fact]
    public async Task A_granted_program_runs_and_returns_its_output_and_exit_code()
    {
        var result = await Granted().RunAsync(_shell, Script("echo hello; exit 3", "echo hello& exit 3"), ct: Ct);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput.Trim());
        Assert.False(result.TimedOut);
        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public async Task A_program_the_module_was_not_granted_is_refused_and_audited_and_never_started()
    {
        var marker = Path.Combine(_work, "ran.txt");
        var (runner, sink) = Make(["process:tools/other"]);

        await Assert.ThrowsAsync<ModulePermissionDeniedException>(
            () => runner.RunAsync(_shell, Script("echo x > ran.txt", "echo x > ran.txt"), ct: Ct));

        Assert.Single(sink.Denials);
        Assert.Equal("process", sink.Denials[0].Class);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_module_with_no_process_permission_cannot_tell_which_programs_exist()
    {
        var (runner, sink) = Make(["fs:local/*"]);

        var known = await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => runner.RunAsync(_shell, [], ct: Ct));
        var unknown = await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => runner.RunAsync("tools/nothing", [], ct: Ct));

        Assert.Equal(known.Message.Replace(_shell, "X", StringComparison.Ordinal), unknown.Message.Replace("tools/nothing", "X", StringComparison.Ordinal));
        Assert.Equal(2, sink.Denials.Count);
    }

    [Fact]
    public async Task A_granted_name_the_host_does_not_offer_is_refused_without_a_host_path()
    {
        var (runner, _) = Make(["process:tools/*"]);

        var e = await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync("tools/missing", [], ct: Ct));

        Assert.Equal("The program is not available.", e.Message);
    }

    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("../shell")]
    [InlineData("tools/../shell")]
    [InlineData("tools\\shell")]
    [InlineData("TOOLS/SHELL")]
    [InlineData("tools/shell/")]
    [InlineData("tools/shell\0")]
    [InlineData("")]
    public async Task A_name_that_is_a_path_or_another_spelling_never_runs_the_program(string name)
    {
        var (runner, sink) = Make(["process:tools/*", "process:" + _shell]);

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => runner.RunAsync(name, Script("echo x > ran.txt", "echo x > ran.txt"), ct: Ct));

        Assert.False(File.Exists(Path.Combine(_work, "ran.txt")));
        Assert.True(sink.Denials.Count <= 1);
    }

    [Fact]
    public async Task Arguments_reach_the_program_one_by_one_and_are_never_read_by_a_shell()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux-only behaviour.");
        // The catalog program here is the shell itself, so only its own argument parsing applies: the hostile text is one
        // argument of "echo" and must come back as text, not run.
        var hostile = OperatingSystem.IsWindows() ? "a b\" & echo injected > ran.txt & \"" : "a b'; echo injected > ran.txt; '";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/d", "/c", "echo", hostile]
            : ["-c", "printf '%s\\n' \"$1\"", "sh", hostile];

        var result = await Granted().RunAsync(_shell, arguments, ct: Ct);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(hostile, result.StandardOutput.TrimEnd('\n'));
            Assert.False(File.Exists(Path.Combine(_work, "ran.txt")));
        }

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task The_servers_environment_does_not_reach_the_program()
    {
        const string name = "BAZAAR_TEST_PROVIDER_KEY";
        Environment.SetEnvironmentVariable(name, "do-not-leak");
        try
        {
            var result = await Granted(environment: new Dictionary<string, string> { ["BAZAAR_TOOL_MODE"] = "quiet" })
                .RunAsync(_shell, Script("env", "set"), ct: Ct);

            Assert.DoesNotContain("do-not-leak", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(name, result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("BAZAAR_TOOL_MODE=quiet", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("USERPROFILE")]
    [InlineData("APPDATA")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("BAZAAR_DB_PASSWORD")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("LD_PRELOAD")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    public void Secrets_profile_folders_and_loader_hooks_are_not_inherited(string name) =>
        Assert.False(ModuleProcessRunner.IsInherited(name));

    [Fact]
    public void The_search_path_is_inherited_in_any_letter_case()
    {
        Assert.True(ModuleProcessRunner.IsInherited("PATH"));
        Assert.True(ModuleProcessRunner.IsInherited("Path"));
    }

    [Fact]
    public async Task The_program_starts_in_the_hosts_working_directory()
    {
        var result = await Granted().RunAsync(_shell, Script("pwd -P", "cd"), ct: Ct);

        var reported = Path.GetFullPath(result.StandardOutput.Trim());
        Assert.Equal(Path.GetFileName(_work), Path.GetFileName(reported));
    }

    [Fact]
    public async Task Input_is_given_to_the_program_and_then_closed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux-only behaviour.");
        var result = await Granted().RunAsync(_shell, Script("cat", "findstr \"^\""), "first\nsecond\n", Ct);

        Assert.Contains("first", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("second", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task A_program_that_waits_for_input_it_was_not_given_sees_the_end_of_input()
    {
        var watch = Stopwatch.StartNew();

        var result = await Granted().RunAsync(_shell, Script("cat", "findstr \"^\""), ct: Ct);

        Assert.False(result.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task Output_past_the_limit_is_dropped_and_reported()
    {
        var runner = Granted(new ModuleProcessOptions(MaxOutputChars: 100));

        var result = await runner.RunAsync(
            _shell,
            Script("i=0; while [ $i -lt 2000 ]; do echo 0123456789012345678901234567890123456789; i=$((i+1)); done", "for /L %i in (1,1,2000) do @echo 0123456789012345678901234567890123456789"),
            ct: Ct);

        Assert.Equal(100, result.StandardOutput.Length);
        Assert.True(result.OutputTruncated);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task A_program_that_never_ends_is_killed_at_the_time_limit()
    {
        var runner = Granted(new ModuleProcessOptions(Timeout: TimeSpan.FromSeconds(2)));
        var watch = Stopwatch.StartNew();

        var result = await runner.RunAsync(_shell, Script("echo started; sleep 120", "echo started& ping -n 120 127.0.0.1 > nul"), ct: Ct);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("started", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task A_cancelled_run_is_killed_and_reports_the_cancel()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var runner = Granted();
        var watch = Stopwatch.StartNew();

        var run = runner.RunAsync(_shell, Script("sleep 120", "ping -n 120 127.0.0.1 > nul"), ct: cancel.Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task One_program_more_than_the_limit_at_once_is_refused_and_the_slot_comes_back()
    {
        var runner = Granted(new ModuleProcessOptions(MaxConcurrent: 1, Timeout: TimeSpan.FromSeconds(30)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = runner.RunAsync(_shell, Script("sleep 120", "ping -n 120 127.0.0.1 > nul"), ct: cancel.Token);
        var e = await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync(_shell, Script("echo x", "echo x"), ct: Ct));
        Assert.Equal("Too many programs are running.", e.Message);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var after = await runner.RunAsync(_shell, Script("echo again", "echo again"), ct: Ct);
        Assert.Equal("again", after.StandardOutput.Trim());
    }

    [Fact]
    public async Task Too_many_or_too_long_arguments_and_nul_characters_are_refused()
    {
        var runner = Granted(new ModuleProcessOptions(MaxArguments: 3, MaxArgumentChars: 10, MaxCommandChars: 15));

        await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync(_shell, ["a", "b", "c", "d"], ct: Ct));
        await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync(_shell, [new string('a', 11)], ct: Ct));
        await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync(_shell, ["aaaaaaaa", "bbbbbbbb"], ct: Ct));
        await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync(_shell, ["a\0b"], ct: Ct));
        await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync(_shell, [null!], ct: Ct));
    }

    [Fact]
    public async Task Input_over_the_limit_is_refused()
    {
        var runner = Granted(new ModuleProcessOptions(MaxInputChars: 4));

        var e = await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync(_shell, Script("cat", "findstr \"^\""), "12345", Ct));

        Assert.Equal("The input is not accepted.", e.Message);
    }

    [Fact]
    public async Task A_program_that_cannot_be_started_is_refused_without_the_host_path()
    {
        var missing = Path.Combine(_work, "no-such-program");
        var (runner, _) = Make(["process:" + _shell], executable: missing);

        var e = await Assert.ThrowsAsync<ModuleProcessRefusedException>(() => runner.RunAsync(_shell, [], ct: Ct));

        Assert.Equal("The program could not be started.", e.Message);
        Assert.DoesNotContain(_work, e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_module_can_run_nothing()
    {
        var sink = new ListSink();
        var gate = new ModulePermissionGate(sink, TimeProvider.System);
        gate.Activate(_module, ["process:" + _shell]);
        var catalog = new ModuleProgramCatalog(new Dictionary<string, ModuleProgram> { [_shell] = new(ShellPath, _work) });
        var runner = new ModuleProcessRunner(gate, _module, catalog);
        gate.Deactivate(_module);

        await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => runner.RunAsync(_shell, Script("echo x", "echo x"), ct: Ct));

        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, Assert.Single(sink.Denials).Decision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/tools/shell")]
    [InlineData("tools/shell/")]
    [InlineData("tools/../shell")]
    [InlineData("tools/*")]
    [InlineData("tools\\shell")]
    public void The_catalog_refuses_a_name_that_is_not_a_plain_permission_target(string name) =>
        Assert.Throws<ArgumentException>(() => new ModuleProgramCatalog(new Dictionary<string, ModuleProgram> { [name] = new(ShellPath, _work) }));

    [Fact]
    public void The_catalog_refuses_relative_paths_and_unsettable_environment_entries()
    {
        Assert.Throws<ArgumentException>(() => new ModuleProgramCatalog(new Dictionary<string, ModuleProgram> { ["a"] = new("sh", _work) }));
        Assert.Throws<ArgumentException>(() => new ModuleProgramCatalog(new Dictionary<string, ModuleProgram> { ["a"] = new(ShellPath, "work") }));
        Assert.Throws<ArgumentException>(() => new ModuleProgramCatalog(new Dictionary<string, ModuleProgram>
        {
            ["a"] = new(ShellPath, _work, new Dictionary<string, string> { ["A=B"] = "x" }),
        }));
        Assert.Throws<ArgumentException>(() => new ModuleProgramCatalog(new Dictionary<string, ModuleProgram>
        {
            ["a"] = new(ShellPath, _work, new Dictionary<string, string> { ["A"] = "x\0y" }),
        }));
    }

    [Fact]
    public void Limits_that_make_no_sense_are_refused_when_the_runner_is_built()
    {
        var gate = new ModulePermissionGate(new ListSink(), TimeProvider.System);
        var catalog = new ModuleProgramCatalog(new Dictionary<string, ModuleProgram>());

        Assert.Throws<ArgumentOutOfRangeException>(() => new ModuleProcessRunner(gate, _module, catalog, new ModuleProcessOptions(MaxConcurrent: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModuleProcessRunner(gate, _module, catalog, new ModuleProcessOptions(Timeout: TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModuleProcessRunner(gate, _module, catalog, new ModuleProcessOptions(Timeout: TimeSpan.FromHours(2))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModuleProcessRunner(gate, _module, catalog, new ModuleProcessOptions(MaxOutputChars: -1)));
    }
}
