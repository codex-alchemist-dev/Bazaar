using Bazaar.Contracts.Vms;

namespace Bazaar.Server.VmTransport.Tests;

/// <summary>Exec through the real guest agent over the loopback transport, and exec ordering against a hand-driven guest.</summary>
public class ChannelExecTests
{
    private static readonly string[] _noEnv = [];

    private static ExecRequest Sh(AgentRig rig, string script, double seconds = 20) =>
        new(["/bin/sh", "-c", script], rig.Root.Root, _noEnv, TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task Exec_streams_output_in_order_and_reports_the_exit_code()
    {
        await using var rig = await AgentRig.StartAsync();
        var chunks = new List<OutputChunk>();

        var result = await rig.Channel.ExecAsync(
            Sh(rig, "echo out1; echo err1 1>&2; exit 3"),
            chunk =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            },
            CancellationToken.None).WithinAsync();

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Equal("out1\n", string.Concat(chunks.Where(c => c.Stream == "stdout").Select(c => c.Text)));
        Assert.Equal("err1\n", string.Concat(chunks.Where(c => c.Stream == "stderr").Select(c => c.Text)));
        Assert.Equal(Enumerable.Range(0, chunks.Count).Select(i => (long)i), chunks.Select(c => c.Seq));
    }

    [Fact]
    public async Task Exec_passes_the_guests_own_value_for_an_allowlisted_variable()
    {
        Environment.SetEnvironmentVariable("BAZAAR_VMT_TEST_VAR", "from-the-guest");
        try
        {
            await using var rig = await AgentRig.StartAsync();
            var text = new System.Text.StringBuilder();
            var request = new ExecRequest(
                ["/bin/sh", "-c", "printf %s \"$BAZAAR_VMT_TEST_VAR\""],
                rig.Root.Root,
                ["BAZAAR_VMT_TEST_VAR"],
                TimeSpan.FromSeconds(20));

            var result = await rig.Channel.ExecAsync(request, c => { text.Append(c.Text); return Task.CompletedTask; }, CancellationToken.None).WithinAsync();

            Assert.True(result.Succeeded);
            Assert.Equal("from-the-guest", text.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_VMT_TEST_VAR", null);
        }
    }

    [Fact]
    public async Task Exec_that_outlives_its_timeout_is_killed_and_reported_timed_out()
    {
        await using var rig = await AgentRig.StartAsync();

        var result = await rig.Channel.ExecAsync(Sh(rig, "sleep 30", seconds: 1), _ => Task.CompletedTask, CancellationToken.None).WithinAsync();

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Cancelling_an_exec_kills_it_and_reports_cancelled()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Needs a POSIX /bin/sh and sleep; the Windows runner's sh does not tie its children to the pipe the way a Linux guest does. Covered on Linux.");
        await using var rig = await AgentRig.StartAsync();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();

        var run = rig.Channel.ExecAsync(
            Sh(rig, "echo started; sleep 30"),
            _ =>
            {
                started.TrySetResult();
                return Task.CompletedTask;
            },
            cts.Token);
        await started.Task.WithinAsync();
        await cts.CancelAsync();
        var result = await run.WithinAsync();

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Exec_in_a_directory_outside_the_allowed_roots_is_refused_with_the_guests_code()
    {
        await using var rig = await AgentRig.StartAsync();
        var request = new ExecRequest(["/bin/sh", "-c", "true"], "/", _noEnv, TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<GuestExecRefusedException>(
            () => rig.Channel.ExecAsync(request, _ => Task.CompletedTask, CancellationToken.None).WithinAsync());

        Assert.Equal(ExecRefusalCodes.CwdNotAllowed, error.Code);
    }

    [Fact]
    public async Task Output_beyond_the_cap_is_dropped_and_flagged()
    {
        await using var rig = await AgentRig.StartAsync(new GuestChannelOptions { MaxOutputBytes = 100 });
        var total = 0;

        var result = await rig.Channel.ExecAsync(
            Sh(rig, "head -c 5000 /dev/zero | tr '\\0' a"),
            chunk =>
            {
                total += chunk.Text.Length;
                return Task.CompletedTask;
            },
            CancellationToken.None).WithinAsync();

        Assert.True(result.OutputTruncated);
        Assert.True(result.Succeeded);
        Assert.Equal(100, total);
    }

    [Fact]
    public async Task A_multibyte_character_split_across_chunks_is_decoded_whole()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var text = new System.Text.StringBuilder();
            var run = channel.ExecAsync(
                new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(5)),
                c => { text.Append(c.Text); return Task.CompletedTask; },
                CancellationToken.None);
            var exec = await rig.ReceiveAsync<Exec>();
            var euro = System.Text.Encoding.UTF8.GetBytes("€");

            await rig.SendAsync(new Output(exec.Id, OutputSource.Stdout, 0, euro[..1]));
            await rig.SendAsync(new Output(exec.Id, OutputSource.Stdout, 1, euro[1..]));
            await rig.SendAsync(new Exit(exec.Id, 0, false));
            await run.WithinAsync();

            Assert.Equal("€", text.ToString());
        }
    }

    [Fact]
    public async Task A_guest_that_never_answers_is_cancelled_after_the_timeout_grace_and_then_given_up_on()
    {
        var options = new GuestChannelOptions { TimeoutGrace = TimeSpan.FromMilliseconds(100), CancelGrace = TimeSpan.FromMilliseconds(100) };
        var (rig, channel) = await ManualRig.OpenAsync(options);
        await using (rig)
        await using (channel)
        {
            var run = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(1)), _ => Task.CompletedTask, CancellationToken.None);
            var exec = await rig.ReceiveAsync<Exec>();
            Assert.Equal(1, exec.TimeoutSec);

            var cancel = await rig.ReceiveAsync<Cancel>();
            var result = await run.WithinAsync();

            Assert.Equal(exec.Id, cancel.Id);
            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task Cancel_sends_a_cancel_frame_and_waits_for_the_guests_exit()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            using var cts = new CancellationTokenSource();
            var run = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, cts.Token);
            var exec = await rig.ReceiveAsync<Exec>();

            await cts.CancelAsync();
            var cancel = await rig.ReceiveAsync<Cancel>();
            Assert.False(run.IsCompleted);
            await rig.SendAsync(new Exit(exec.Id, 137, false));
            var result = await run.WithinAsync();

            Assert.Equal(exec.Id, cancel.Id);
            Assert.True(result.Cancelled);
            Assert.Equal(137, result.ExitCode);
        }
    }

    [Fact]
    public async Task A_timeout_that_races_a_cancel_is_reported_as_a_timeout_not_a_cancel()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            using var cts = new CancellationTokenSource();
            var run = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, cts.Token);
            var exec = await rig.ReceiveAsync<Exec>();

            await cts.CancelAsync();
            await rig.ReceiveAsync<Cancel>();
            await rig.SendAsync(new Exit(exec.Id, -1, TimedOut: true));
            var result = await run.WithinAsync();

            Assert.True(result.TimedOut);
            Assert.False(result.Cancelled);
        }
    }

    [Fact]
    public async Task Frames_for_an_exec_that_was_given_up_on_are_ignored_without_closing_the_channel()
    {
        var options = new GuestChannelOptions { TimeoutGrace = TimeSpan.FromMilliseconds(50), CancelGrace = TimeSpan.FromMilliseconds(50) };
        var (rig, channel) = await ManualRig.OpenAsync(options);
        await using (rig)
        await using (channel)
        {
            var run = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(1)), _ => Task.CompletedTask, CancellationToken.None);
            var exec = await rig.ReceiveAsync<Exec>();
            await rig.ReceiveAsync<Cancel>();
            await run.WithinAsync();

            await rig.SendAsync(new Output(exec.Id, OutputSource.Stdout, 0, [1, 2, 3]));
            await rig.SendAsync(new Exit(exec.Id, 0, false));
            await rig.SendAsync(new Heartbeat());

            Assert.Equal(GuestChannelState.Open, channel.State);
        }
    }

    [Fact]
    public async Task A_callback_that_throws_stops_the_exec_and_the_error_reaches_the_caller()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var run = channel.ExecAsync(
                new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(30)),
                _ => throw new InvalidOperationException("callback broke"),
                CancellationToken.None);
            var exec = await rig.ReceiveAsync<Exec>();

            await rig.SendAsync(new Output(exec.Id, OutputSource.Stdout, 0, [65]));
            await rig.ReceiveAsync<Cancel>();
            await rig.SendAsync(new Exit(exec.Id, 0, false));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WithinAsync());
            Assert.Equal("callback broke", error.Message);
        }
    }

    [Fact]
    public async Task Execs_over_the_channels_own_cap_are_refused_before_anything_is_sent()
    {
        var (rig, channel) = await ManualRig.OpenAsync(new GuestChannelOptions { MaxConcurrentExecs = 1 });
        await using (rig)
        await using (channel)
        {
            var first = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, CancellationToken.None);
            var exec = await rig.ReceiveAsync<Exec>();

            var error = await Assert.ThrowsAsync<GuestExecRefusedException>(
                () => channel.ExecAsync(new ExecRequest(["y"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, CancellationToken.None));

            Assert.Equal(ExecRefusalCodes.TooManyExecs, error.Code);
            await rig.SendAsync(new Exit(exec.Id, 0, false));
            await first.WithinAsync();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100000)]
    public async Task A_timeout_outside_the_allowed_range_is_rejected(int seconds)
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(seconds)), _ => Task.CompletedTask, CancellationToken.None));
        }
    }

    [Fact]
    public async Task An_exec_in_flight_when_the_channel_ends_fails_with_a_channel_exception()
    {
        var (rig, channel) = await ManualRig.OpenAsync();
        await using (rig)
        await using (channel)
        {
            var run = channel.ExecAsync(new ExecRequest(["x"], "/", _noEnv, TimeSpan.FromSeconds(30)), _ => Task.CompletedTask, CancellationToken.None);
            await rig.ReceiveAsync<Exec>();

            await rig.CloseGuestAsync();

            await Assert.ThrowsAsync<GuestChannelException>(() => run.WithinAsync());
            Assert.Equal(GuestChannelState.Closed, channel.State);
        }
    }
}
