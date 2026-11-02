using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests;

public sealed class ExecTests
{
    [Fact]
    public async Task Exec_streams_stdout_and_stderr_and_reports_the_exit_code()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(Shell(root, "e1", "echo out; echo err 1>&2; exit 3"));
        var result = await guest.CollectExecAsync("e1").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.Equal("out\n", result.Text(OutputSource.Stdout));
        Assert.Equal("err\n", result.Text(OutputSource.Stderr));
        Assert.Equal(3, result.Exit.Code);
        Assert.False(result.Exit.TimedOut);
    }

    [Fact]
    public async Task Output_sequence_numbers_increase_per_stream_from_zero()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(Shell(root, "e2", "echo a; echo b 1>&2; echo c"));
        var result = await guest.CollectExecAsync("e2").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        AssertContiguous(result.Seqs(OutputSource.Stdout));
        AssertContiguous(result.Seqs(OutputSource.Stderr));
    }

    [Fact]
    public async Task Large_output_is_split_into_chunks_under_the_frame_cap()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(Shell(root, "e3", "head -c 3000000 /dev/zero"));
        var result = await guest.CollectExecAsync("e3").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.Equal(3_000_000, result.Bytes(OutputSource.Stdout).Length);
        Assert.True(result.Outputs.Count > 1);
        Assert.All(result.Outputs, output => Assert.True(output.Bytes.Length <= 32 * 1024));
        AssertContiguous(result.Seqs(OutputSource.Stdout));
    }

    [Fact]
    public async Task Exec_runs_in_the_requested_working_directory()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(Shell(root, "e4", "pwd"));
        var result = await guest.CollectExecAsync("e4").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFileName(root.Root), Path.GetFileName(result.Text(OutputSource.Stdout).Trim()));
    }

    [Fact]
    public async Task Env_allowlist_passes_only_the_named_variables()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        const string allowed = "BAZAAR_GUEST_TEST_ALLOWED";
        const string denied = "BAZAAR_GUEST_TEST_DENIED";
        Environment.SetEnvironmentVariable(allowed, "yes");
        Environment.SetEnvironmentVariable(denied, "no");
        try
        {
            await using var root = new TempRoot();
            await using var guest = new GuestHarness(root.Options());
            await guest.ReceiveAsync<Hello>();

            var message = new Exec(
                "e5",
                ["/bin/sh", "-c", $"echo ${allowed}-${denied}"],
                root.Root,
                [allowed],
                30);
            await guest.SendAsync(message);
            var result = await guest.CollectExecAsync("e5").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

            Assert.Equal("yes-\n", result.Text(OutputSource.Stdout));
        }
        finally
        {
            Environment.SetEnvironmentVariable(allowed, null);
            Environment.SetEnvironmentVariable(denied, null);
        }
    }

    [Fact]
    public async Task Timeout_kills_the_whole_process_tree()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        // The background sleep inherits stdout, so Exit can only be sent once it is dead. An Exit within the bound
        // therefore shows the grandchild was killed too, not just the shell.
        await guest.SendAsync(Shell(root, "e6", "sleep 100 & sleep 100", timeoutSec: 1));
        var result = await guest.CollectExecAsync("e6").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.True(result.Exit.TimedOut);
        Assert.NotEqual(0, result.Exit.Code);
    }

    [Fact]
    public async Task Cancel_kills_the_tree_and_reports_a_nonzero_exit()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(Shell(root, "e7", "echo started; sleep 100 & sleep 100", timeoutSec: 300));
        await guest.ReceiveFirstOutputAsync("e7");
        await guest.SendAsync(new Cancel("e7"));
        var result = await guest.CollectExecAsync("e7").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.False(result.Exit.TimedOut);
        Assert.NotEqual(0, result.Exit.Code);
    }

    [Fact]
    public async Task Cancel_of_an_unknown_id_is_ignored()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Cancel("no-such-exec"));

        // Nothing is sent back for the cancel, so the next frame is the heartbeat echo.
        await guest.SyncAsync();
    }

    [Fact]
    public async Task Exec_over_the_concurrency_cap_is_refused_with_an_error()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options(maxExecs: 2));
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(Shell(root, "a", "sleep 100"));
        await guest.SendAsync(Shell(root, "b", "sleep 100"));
        await guest.SendAsync(Shell(root, "c", "true"));

        var error = await guest.ReceiveAsync<ExecError>();
        Assert.Equal("c", error.Id);
        Assert.Equal(ExecRefusalCodes.TooManyExecs, error.Code);
        Assert.Contains("too many concurrent execs", error.Message);

        var refused = await guest.CollectExecAsync("c").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);
        Assert.Equal(-1, refused.Exit.Code);
        Assert.False(refused.Exit.TimedOut);
        Assert.Empty(refused.Outputs);

        await guest.SendAsync(new Cancel("a"));
        await guest.SendAsync(new Cancel("b"));
        await guest.CollectExecAsync("a").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);
        await guest.CollectExecAsync("b").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_program_ends_with_a_nonzero_exit()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Exec("e8", ["/nonexistent/bazaar-no-such-program"], root.Root, [], 30));
        var error = await guest.ReceiveAsync<ExecError>();
        Assert.Equal(ExecRefusalCodes.ProgramNotStarted, error.Code);

        var result = await guest.CollectExecAsync("e8").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.Exit.Code);
        Assert.False(result.Exit.TimedOut);
    }

    [Fact]
    public async Task Exec_runs_a_program_and_reports_its_exit_code_on_every_platform()
    {
        // Runs on every platform, so the Windows path (no setsid, the program started directly) is covered too.
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var argv = OperatingSystem.IsWindows()
            ? new[] { "cmd.exe", "/c", "echo out && exit 3" }
            : new[] { "/bin/sh", "-c", "echo out && exit 3" };

        await guest.SendAsync(new Exec("e10", argv, root.Root, [], 30));
        var result = await guest.CollectExecAsync("e10").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.Equal("out", result.Text(OutputSource.Stdout).Trim());
        Assert.Equal(3, result.Exit.Code);
        Assert.False(result.Exit.TimedOut);
    }

    [Fact]
    public async Task Exec_in_a_subdirectory_of_a_root_runs_there()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux process tooling required");
        await using var root = new TempRoot();
        var sub = Path.Combine(root.Root, "sub");
        Directory.CreateDirectory(sub);
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Exec("e9", ["/bin/sh", "-c", "pwd"], sub, [], 30));
        var result = await guest.CollectExecAsync("e9").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Exit.Code);
        Assert.Equal("sub", Path.GetFileName(result.Text(OutputSource.Stdout).Trim()));
    }

    [Fact]
    public async Task Exec_with_a_working_directory_outside_the_roots_is_refused()
    {
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Exec("c1", ["/bin/sh", "-c", "pwd"], outside.Root, [], 30));

        await ExpectRefusalAsync(guest, "c1", ExecRefusalCodes.CwdNotAllowed);
    }

    [Fact]
    public async Task Exec_with_no_roots_configured_is_refused_for_every_working_directory()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options() with { AllowedRoots = [] });
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Exec("c2", ["/bin/sh", "-c", "true"], root.Root, [], 30));

        await ExpectRefusalAsync(guest, "c2", ExecRefusalCodes.CwdNotAllowed);
    }

    [Fact]
    public async Task Exec_with_a_dotdot_escape_from_the_root_is_refused()
    {
        await using var root = new TempRoot();
        await using var sibling = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var escaped = Path.Combine(root.Root, "..", Path.GetFileName(sibling.Root));

        await guest.SendAsync(new Exec("c3", ["/bin/sh", "-c", "pwd"], escaped, [], 30));

        await ExpectRefusalAsync(guest, "c3", ExecRefusalCodes.CwdNotAllowed);
    }

    [Fact]
    public async Task Exec_with_a_symlink_working_directory_that_leaves_the_root_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        File.CreateSymbolicLink(Path.Combine(root.Root, "link"), outside.Root);
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Exec("c4", ["/bin/sh", "-c", "pwd"], Path.Combine(root.Root, "link"), [], 30));

        await ExpectRefusalAsync(guest, "c4", ExecRefusalCodes.CwdNotAllowed);
    }

    [Fact]
    public async Task Exec_with_a_non_positive_timeout_is_refused()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new Exec("c5", ["/bin/sh", "-c", "true"], root.Root, [], 0));

        await ExpectRefusalAsync(guest, "c5", ExecRefusalCodes.TimeoutNotPositive);
    }

    /// <summary>Reads the ExecError for a refused exec, then its Exit, which must be -1 with no output.</summary>
    private static async Task ExpectRefusalAsync(GuestHarness guest, string id, string code)
    {
        var error = await guest.ReceiveAsync<ExecError>();
        Assert.Equal(id, error.Id);
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrEmpty(error.Message));

        var result = await guest.CollectExecAsync(id).WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);
        Assert.Equal(-1, result.Exit.Code);
        Assert.False(result.Exit.TimedOut);
        Assert.Empty(result.Outputs);
    }

    private static Exec Shell(TempRoot root, string id, string script, int timeoutSec = 30) =>
        new(id, ["/bin/sh", "-c", script], root.Root, [], timeoutSec);

    private static void AssertContiguous(IReadOnlyList<long> seqs)
    {
        for (var i = 0; i < seqs.Count; i++)
        {
            Assert.Equal((long)i, seqs[i]);
        }
    }
}
