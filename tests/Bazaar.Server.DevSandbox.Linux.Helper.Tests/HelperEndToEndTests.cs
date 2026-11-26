namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests;

/// <summary>
/// Runs the real helper binary against this kernel. Each test skips (and says why) when the kernel has no Landlock; none passes by
/// default. SIGSYS kills show up as exit code 159 (128 + 31).
/// </summary>
public sealed class HelperEndToEndTests
{
    private const int _sigsysExit = 128 + 31;

    private static void Require() => Assert.SkipUnless(HelperHost.RequireSkipReason().Length == 0, HelperHost.RequireSkipReason());

    private static void RequireTools() =>
        Assert.SkipUnless(File.Exists("/bin/sh") && File.Exists("/usr/bin/cat") && File.Exists("/usr/bin/env") && File.Exists("/usr/bin/true"), "sh, cat, env or true missing");

    [Fact]
    public async Task Info_reports_the_landlock_abi_and_applies_nothing()
    {
        Assert.SkipUnless(File.Exists(HelperHost.HelperPath), "helper not built");
        var r = await HelperHost.RunAsync(["--info"], null);

        Assert.Equal(0, r.ExitCode);
        Assert.Matches(@"landlock-abi=-?\d+", r.Stdout);
        Assert.Contains("arch=", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_allowed_target_runs_and_its_output_comes_through()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunTargetAsync("/bin/sh", "-c", "echo hello-from-target");

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("hello-from-target", r.Stdout.Trim());
    }

    [Fact]
    public async Task Landlock_denies_reading_a_file_outside_the_allowed_paths()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        var canary = Path.Combine(sb.Outside, "canary.txt");

        var r = await sb.RunTargetAsync("/usr/bin/cat", canary);

        Assert.NotEqual(0, r.ExitCode);
        Assert.DoesNotContain("outside-secret", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("Permission denied", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Landlock_denies_writing_outside_the_work_directory_and_allows_writing_inside_it()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        var outsideFile = Path.Combine(sb.Outside, "written.txt");
        var insideFile = Path.Combine(sb.Work, "inside.txt");

        var r = await sb.RunTargetAsync("/bin/sh", "-c", $"echo x > '{outsideFile}'; echo outside=$?; echo y > '{insideFile}'; echo inside=$?; mkdir '{sb.Work}/sub' && echo mkdir=ok; rm '{insideFile}' && echo rm=ok");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("outside=2", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("inside=0", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("mkdir=ok", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("rm=ok", r.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(outsideFile));
        Assert.True(Directory.Exists(Path.Combine(sb.Work, "sub")));
    }

    [Fact]
    public async Task Landlock_denies_deleting_and_listing_outside_files()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        var canary = Path.Combine(sb.Outside, "canary.txt");

        var r = await sb.RunTargetAsync("/bin/sh", "-c", $"rm '{canary}'; echo rm=$?; for f in '{sb.Outside}'/*; do echo listed=$f; done");

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("rm=0", r.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(canary));
        Assert.DoesNotContain("canary.txt", r.Stdout.Replace("listed=" + sb.Outside + "/*", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execve_works_for_the_target_and_for_a_nested_fork_and_exec()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunTargetAsync("/bin/sh", "-c", "/usr/bin/true && echo nested-exec-ok; /usr/bin/env > /dev/null && echo nested-env-ok");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("nested-exec-ok", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("nested-env-ok", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_written_into_the_work_directory_cannot_be_executed()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        var copy = Path.Combine(sb.Work, "mytrue");

        var r = await sb.RunTargetAsync("/bin/sh", "-c", $"cp /usr/bin/true '{copy}' && chmod +x '{copy}' && '{copy}'; echo run=$?");

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("run=0", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_target_environment_holds_only_path()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        var env = new Dictionary<string, string> { ["BAZAAR_SECRET"] = "leak-me", ["HOME"] = "/root" };

        var r = await sb.RunAsync(["--", "/usr/bin/env"], env: env);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("PATH=/usr/bin:/bin", r.Stdout.Trim());
    }

    [Fact]
    public async Task The_path_in_the_policy_is_what_the_target_gets()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunAsync(["--", "/usr/bin/env"], sb.PolicyJson(extra: ",\"path\":\"/opt/x:/usr/bin\""));

        Assert.Equal("PATH=/opt/x:/usr/bin", r.Stdout.Trim());
    }

    [Fact]
    public async Task No_new_privs_is_set_and_a_seccomp_filter_is_active_in_the_target()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunTargetAsync("/bin/sh", "-c", "while read -r k v; do case $k in NoNewPrivs:|Seccomp:) echo \"$k $v\";; esac; done < /proc/self/status");

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("NoNewPrivs: 1", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("Seccomp: 2", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seccomp_kills_a_denied_syscall_with_sigsys()
    {
        Require();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunAsync(["--self-test", "socket"]);

        Assert.Equal(_sigsysExit, r.ExitCode);
        Assert.Contains("ready", r.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("SOCKET_ALLOWED", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_seccomp_filter_is_inherited_across_exec_so_a_target_that_opens_a_socket_is_killed()
    {
        Require();
        Assert.SkipUnless(File.Exists("/usr/bin/bash"), "bash not installed: cannot make an exec'd target call socket()");
        using var sb = new HelperHost.Sandbox();

        // bash opens /dev/tcp/... with socket(2) and connect(2).
        var r = await sb.RunTargetAsync("/usr/bin/bash", "-c", "echo before; exec 3<>/dev/tcp/127.0.0.1/9; echo after-socket");

        Assert.Equal(_sigsysExit, r.ExitCode);
        Assert.DoesNotContain("after-socket", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_address_space_limit_from_the_policy_is_applied_to_the_target()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        const long Limit = 512L * 1024 * 1024;

        var r = await sb.RunAsync(
            ["--", "/bin/sh", "-c", "while read -r a b c d e f; do if [ \"$a $b $c\" = \"Max address space\" ]; then echo \"AS=$d,$e\"; fi; done < /proc/self/limits"],
            sb.PolicyJson(extra: ",\"addressSpaceBytes\":" + Limit));

        Assert.Equal(0, r.ExitCode);
        Assert.Equal($"AS={Limit},{Limit}", r.Stdout.Trim());
    }

    [Fact]
    public async Task The_helper_exits_70_without_running_the_target_when_landlock_is_unavailable()
    {
        Assert.SkipUnless(File.Exists(HelperHost.HelperPath), "helper not built");
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        var marker = Path.Combine(sb.Work, "ran.txt");

        var r = await sb.RunAsync(["--simulate-landlock-unavailable", "--", "/bin/sh", "-c", $"echo ran > '{marker}'"]);

        Assert.Equal(70, r.ExitCode);
        Assert.Contains("Landlock is not available", r.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        Assert.Equal(string.Empty, r.Stdout);
    }

    [Fact]
    public async Task An_invalid_policy_exits_65_and_a_missing_policy_exits_77_without_running_the_target()
    {
        Assert.SkipUnless(File.Exists(HelperHost.HelperPath), "helper not built");
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        var marker = Path.Combine(sb.Work, "ran.txt");

        var invalid = await sb.RunAsync(["--", "/bin/sh", "-c", $"echo ran > '{marker}'"], "{\"version\":1,\"writePaths\":[\"/\"]}");
        var missing = await HelperHost.RunAsync(["--policy", Path.Combine(sb.Root, "absent.json"), "--", "/bin/sh", "-c", $"echo ran > '{marker}'"], sb.Work);

        Assert.Equal(65, invalid.ExitCode);
        Assert.Equal(77, missing.ExitCode);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_usage_error_exits_64()
    {
        Assert.SkipUnless(File.Exists(HelperHost.HelperPath), "helper not built");

        var r = await HelperHost.RunAsync(["--bogus"], null);

        Assert.Equal(64, r.ExitCode);
    }

    [Fact]
    public async Task A_missing_write_path_exits_71_and_does_not_run_the_target()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();
        var marker = Path.Combine(sb.Work, "ran.txt");
        var json = "{\"version\":1,\"readPaths\":[\"/usr/bin\",\"/usr/lib\",\"/lib\",\"/lib64\"],\"writePaths\":[\"" + Path.Combine(sb.Root, "does-not-exist") + "\"]}";

        var r = await sb.RunAsync(["--", "/bin/sh", "-c", $"echo ran > '{marker}'"], json);

        Assert.Equal(71, r.ExitCode);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_target_that_cannot_be_executed_exits_74_with_a_message()
    {
        Require();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunTargetAsync("/usr/bin/definitely-not-a-program");

        Assert.Equal(74, r.ExitCode);
        Assert.Contains("execve failed", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_target_outside_the_read_paths_cannot_be_executed()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunAsync(["--", "/usr/bin/true"], sb.PolicyJson(readPaths: ["/usr/lib", "/lib", "/lib64", "/usr/lib64"]));

        Assert.Equal(74, r.ExitCode);
    }

    [Fact]
    public async Task The_policy_file_is_deleted_before_the_target_runs_when_asked()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunAsync(["--delete-policy-after-read", "--", "/bin/sh", "-c", $"[ -e '{sb.PolicyFile}' ] && echo present || echo gone"]);

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("gone", r.Stdout.Trim());
        Assert.False(File.Exists(sb.PolicyFile));
    }

    [Fact]
    public async Task No_extra_file_descriptors_reach_the_target()
    {
        Require();
        RequireTools();
        using var sb = new HelperHost.Sandbox();

        var r = await sb.RunTargetAsync("/bin/sh", "-c", "n=0; for f in /proc/self/fd/*; do n=$((n+1)); done; echo fds=$n");

        Assert.Equal(0, r.ExitCode);
        // 0, 1, 2 and the directory handle the shell opens to list its own fd directory.
        var count = int.Parse(r.Stdout.Trim()["fds=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(count, 3, 5);
    }
}
