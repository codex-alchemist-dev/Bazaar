using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.Portability.Sandbox.Tests.Adversarial;

// Red team: the parser child has been exploited and lies over the protocol. Every attack must end as a typed failure,
// with the child's whole tree killed, in bounded time and bounded server memory.
public class HostileChildProtocolTests
{
    private static ImportSource Source(string name = "in.txt") => new(name, Encoding.UTF8.GetBytes("hello"));

    private static ParserSandboxException ParseFails(HostileChild child, Func<ParserSandboxOptions, ParserSandboxOptions>? tune = null, ImportSource? source = null)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "the hostile child is a /bin/sh script");
        var runner = HostileChild.Runner(child, tune);
        return Assert.Throws<ParserSandboxException>(() => runner.Parse("any", source ?? Source()));
    }

    [Fact]
    public void An_oversize_length_prefix_is_refused_without_allocating()
    {
        var error = ParseFails(new HostileChild(HostileChild.Send([0xFF, 0xFF, 0xFF, 0x7F]) + " sleep 60"));

        Assert.Equal(ParserFailureKind.OutputTooLarge, error.Kind);
    }

    [Fact]
    public void A_negative_length_prefix_is_refused()
    {
        var error = ParseFails(new HostileChild(HostileChild.Send([0xFF, 0xFF, 0xFF, 0xFF]) + " sleep 60"));

        Assert.Equal(ParserFailureKind.OutputTooLarge, error.Kind);
    }

    [Fact]
    public void Garbage_before_the_first_frame_is_a_failure_not_a_hang()
    {
        var started = Stopwatch.StartNew();

        var error = ParseFails(new HostileChild("echo 'this is not a frame, hello'; sleep 60"));

        Assert.Equal(ParserFailureKind.OutputTooLarge, error.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void A_frame_that_never_ends_is_cut_at_the_frame_gap_timeout()
    {
        var started = Stopwatch.StartNew();
        // One good record, then a header announcing 1,000,000 bytes and one byte every 300 ms.
        var script = HostileChild.Send(HostileChild.Frame(HostileChild.RecordJson()))
            + " printf '\\100\\102\\017\\000'; while :; do printf x; sleep 0.3; done";

        var error = ParseFails(new HostileChild(script, viaLinuxProvider: true), o => o with { FrameGapTimeout = TimeSpan.FromSeconds(1) });

        Assert.Equal(ParserFailureKind.Timeout, error.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), "the drip must not outlast the frame gap");
    }

    [Fact]
    public void A_drip_before_any_frame_ends_at_the_job_deadline()
    {
        var started = Stopwatch.StartNew();
        var script = "printf '\\100\\102\\017\\000'; while :; do printf x; sleep 0.3; done";

        var error = ParseFails(new HostileChild(script, viaLinuxProvider: true), o => o with { JobTimeout = TimeSpan.FromSeconds(2) });

        Assert.Equal(ParserFailureKind.JobTimeout, error.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Valid_frames_forever_hit_the_output_cap()
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-hostile-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, HostileChild.Frame(HostileChild.RecordJson(new string('z', 1000))));
        var script = $"while :; do cat '{path}' || exit 1; done";

        try
        {
            var error = ParseFails(new HostileChild(script), o => o with { MaxOutputBytes = 5 * 1024 * 1024 });

            Assert.Equal(ParserFailureKind.OutputTooLarge, error.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tiny_valid_record_frames_forever_hit_the_record_count_cap()
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-hostile-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, [.. Enumerable.Repeat(HostileChild.Frame(HostileChild.RecordJson("")), 2000).SelectMany(f => f)]);
        try
        {
            var error = ParseFails(new HostileChild($"while :; do cat '{path}' || exit 1; done"), o => o with { MaxRecords = 1000 });

            Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_second_result_after_done_is_ignored_and_the_child_is_ended()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "the hostile child is a /bin/sh script");
        var script = HostileChild.Send([.. HostileChild.Frame(HostileChild.DoneZero), .. HostileChild.Frame(HostileChild.RecordJson("late lie"))]) + " sleep 120";
        var runner = HostileChild.Runner(new HostileChild(script, viaLinuxProvider: true));
        var started = Stopwatch.StartNew();

        var records = runner.Parse("any", Source());

        Assert.Empty(records);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), "a child that lingers after its result must be killed");
    }

    [Fact]
    public void A_detect_frame_in_a_parse_job_is_a_protocol_violation()
    {
        // The protocol has no request id (one child per request); the op is the cross-check.
        var frame = HostileChild.Frame("{\"type\":\"detect\",\"confidence\":1,\"reason\":\"x\"}");

        var error = ParseFails(new HostileChild(HostileChild.Send(frame) + " sleep 60"));

        Assert.Equal(ParserFailureKind.ProtocolViolation, error.Kind);
    }

    [Fact]
    public void Invalid_utf8_in_a_frame_is_a_protocol_violation()
    {
        var json = Encoding.UTF8.GetBytes(HostileChild.RecordJson("AAAA"));
        var at = json.AsSpan().IndexOf("AAAA"u8);
        json[at] = 0xFF;
        json[at + 1] = 0xFE;

        var error = ParseFails(new HostileChild(HostileChild.Send(HostileChild.Frame(json)) + " sleep 60"));

        Assert.Equal(ParserFailureKind.ProtocolViolation, error.Kind);
    }

    [Fact]
    public void Deeply_nested_json_is_refused_before_the_deserializer_sees_it()
    {
        var nested = "{\"type\":\"record\",\"x\":" + new string('[', 200_000) + new string(']', 200_000) + "}";

        var error = ParseFails(new HostileChild(HostileChild.Send(HostileChild.Frame(nested)) + " sleep 60"));

        Assert.Equal(ParserFailureKind.ProtocolViolation, error.Kind);
    }

    [Fact]
    public void Ten_million_tiny_items_are_refused_by_the_token_cap()
    {
        var sb = new StringBuilder("{\"type\":\"record\",\"record\":{\"sourceKey\":\"k\",\"kind\":\"d\",\"title\":\"t\",\"body\":\"b\",\"attributes\":{");
        for (var i = 0; i < 300_000; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(i).Append("\":\"v\"");
        }
        sb.Append("}}}");

        var error = ParseFails(new HostileChild(HostileChild.Send(HostileChild.Frame(sb.ToString())) + " sleep 60"));

        Assert.Equal(ParserFailureKind.ProtocolViolation, error.Kind);
    }

    [Fact]
    public void A_huge_string_in_a_frame_hits_the_record_limit()
    {
        var frame = HostileChild.Frame(HostileChild.RecordJson(new string('h', 1_500_000)));

        var error = ParseFails(new HostileChild(HostileChild.Send(frame) + " sleep 60"), o => o with { Limits = new ImportLimits(maxRecordBytes: 1024 * 1024) });

        Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void Megabytes_of_stderr_neither_block_the_child_nor_balloon_the_server()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "the hostile child is a /bin/sh script");
        var script = "head -c 60000000 /dev/zero | tr '\\0' 'e' >&2; " + HostileChild.Send(HostileChild.Frame(HostileChild.DoneZero));
        var runner = HostileChild.Runner(new HostileChild(script));
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var records = runner.Parse("any", Source());

        Assert.Empty(records);
        Assert.True(GC.GetTotalMemory(forceFullCollection: true) - before < 40_000_000, "stderr must be capped to a short tail");
    }

    [Fact]
    public void Exiting_zero_without_a_result_is_a_crash()
    {
        Assert.Equal(ParserFailureKind.Crash, ParseFails(new HostileChild("exit 0")).Kind);
    }

    [Fact]
    public void Dying_by_a_signal_is_a_crash()
    {
        // SIGSEGV, not SIGKILL: an exit by SIGKILL that the server did not send is read as the kernel's out-of-memory kill (a limit).
        Assert.Equal(ParserFailureKind.Crash, ParseFails(new HostileChild("kill -11 $$")).Kind);
    }

    [Fact]
    public void Closing_stdout_but_running_on_is_killed_at_once()
    {
        var marker = "8" + Random.Shared.Next(100000, 999999) + ".5";
        var started = Stopwatch.StartNew();

        var error = ParseFails(new HostileChild($"exec 1>&-; /bin/sleep {marker} & wait", viaLinuxProvider: true));

        Assert.Equal(ParserFailureKind.Crash, error.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20));
        Assert.False(HostileChild.ProcessWithArgumentExists(marker), "the lingering process must die with the tree");
    }

    [Fact]
    public void A_grandchild_that_outlives_the_child_and_holds_the_pipe_does_not_stall_the_read()
    {
        var marker = "9" + Random.Shared.Next(100000, 999999) + ".5";
        var started = Stopwatch.StartNew();

        var error = ParseFails(new HostileChild($"/bin/sleep {marker} & exit 0", viaLinuxProvider: true), o => o with { JobTimeout = TimeSpan.FromMinutes(5) });

        Assert.Equal(ParserFailureKind.Crash, error.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), "the read must be abandoned soon after the child's exit, not at the 5 minute job timeout");
        // Known gap: the group is not signalled once its leader has exited (its pid may have been reused), so the orphan itself
        // lives on until it ends. Reaching it needs a PID namespace with an init process in front of the parser.
        HostileChild.KillProcessesWithArgument(marker);
    }

    [Fact]
    public void A_busy_loop_is_ended_by_the_wall_clock_deadline()
    {
        var started = Stopwatch.StartNew();

        var error = ParseFails(new HostileChild("while :; do :; done", viaLinuxProvider: true), o => o with { JobTimeout = TimeSpan.FromSeconds(2) });

        Assert.Equal(ParserFailureKind.JobTimeout, error.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cancellation_kills_the_whole_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "the hostile child is a /bin/sh script");
        var marker = "6" + Random.Shared.Next(100000, 999999) + ".5";
        var runner = HostileChild.Runner(new HostileChild($"/bin/sleep {marker} & wait", viaLinuxProvider: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ParseAsync("any", Source(), cts.Token));

        Assert.False(HostileChild.ProcessWithArgumentExists(marker));
    }

    [Fact]
    public void The_guard_is_disposed_exactly_once_on_every_path()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "the hostile child is a /bin/sh script");
        string[] scripts =
        [
            HostileChild.Send(HostileChild.Frame(HostileChild.DoneZero)),
            "exit 3",
            "echo garbage-garbage",
            HostileChild.Send(HostileChild.Frame("{\"type\":\"nope\"}")) + " sleep 30",
            "sleep 60",
        ];
        foreach (var script in scripts)
        {
            var child = new HostileChild(script);
            var runner = HostileChild.Runner(child, o => o with { JobTimeout = TimeSpan.FromSeconds(1) });

            try
            {
                runner.Parse("any", Source());
            }
            catch (ParserSandboxException)
            {
                // Expected for the failing scripts.
            }

            Assert.Equal(1, child.Disposed);
            Assert.True(child.Killed >= 1, "the tree is killed before the guard is released");
        }
    }

    [Fact]
    public void The_file_name_never_reaches_the_child_command_line_so_it_cannot_be_read_as_an_option()
    {
        var child = new HostileChild(HostileChild.Send(HostileChild.Frame(HostileChild.DoneZero)));
        var runner = HostileChild.Runner(child);

        runner.Parse("any", new ImportSource("--importer-assembly=/tmp/evil.dll", Encoding.UTF8.GetBytes("x")));

        // Prepare saw the real launch command; only the host path and the operator's importer assemblies are arguments.
        Assert.DoesNotContain(child.Prepared.Single(), a => a.Contains("evil", StringComparison.Ordinal));
    }
}
