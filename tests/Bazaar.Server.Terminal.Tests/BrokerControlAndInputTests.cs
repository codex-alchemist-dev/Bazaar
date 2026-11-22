using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Tests;

public class BrokerControlAndInputTests
{
    private static async Task<(Harness H, TerminalInfo Info)> WithHumanWriterAsync(TerminalOptions? options = null)
    {
        var h = new Harness(options);
        h.Grants.Write.Add("human");
        var info = await h.OpenAsync();
        return (h, info);
    }

    [Fact]
    public async Task Take_is_refused_while_another_member_holds_control()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            var result = await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
            Assert.Equal(TerminalRefusal.WriterHeld, result.Refusal);
        }
    }

    [Fact]
    public async Task Release_then_take_hands_over_and_both_are_audited()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            Assert.True((await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct)).Ok);
            Assert.True((await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct)).Ok);
            Assert.Equal("human", h.Broker.List(Harness.Owner)[0].WriterMemberId);
            Assert.Equal(["open", "control.release", "control.take"], h.Audit.Kinds);
            Assert.Equal("human", h.Audit.Rows[^1].Member);
        }
    }

    [Fact]
    public async Task Taking_control_you_already_hold_is_idempotent_and_not_audited_twice()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
            await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
            Assert.True((await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct)).Ok);
            Assert.Single(h.Audit.Kinds, k => k == "control.take");
        }
    }

    [Fact]
    public async Task Taking_control_is_refused_without_a_write_grant_even_when_nobody_holds_it()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
        var result = await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
        Assert.Equal(TerminalRefusal.Forbidden, result.Refusal);
        Assert.Null(h.Broker.List(Harness.Owner)[0].WriterMemberId);
    }

    [Fact]
    public async Task Viewer_with_only_a_view_grant_cannot_take_control()
    {
        await using var h = new Harness();
        h.Grants.View.Add("human");
        var info = await h.OpenAsync();
        await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct)).Refusal);
    }

    [Fact]
    public async Task Human_take_is_refused_and_rolled_back_when_audit_is_down()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
            h.Audit.Fail = true;
            var result = await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
            Assert.Equal(TerminalRefusal.Forbidden, result.Refusal);
            Assert.Null(h.Broker.List(Harness.Owner)[0].WriterMemberId);
        }
    }

    [Fact]
    public async Task Release_by_a_non_writer_is_refused_but_the_owner_may_force_it()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.ReleaseControlAsync(Harness.Human, info.TerminalId, Harness.Ct)).Refusal);
            Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.ReleaseControlAsync(Harness.Agent2, info.TerminalId, Harness.Ct)).Refusal);
            Assert.True((await h.Broker.ReleaseControlAsync(Harness.Owner, info.TerminalId, Harness.Ct)).Ok);
            Assert.Null(h.Broker.List(Harness.Owner)[0].WriterMemberId);
        }
    }

    [Fact]
    public async Task An_agent_claiming_to_be_owner_cannot_force_release()
    {
        await using var h = new Harness();
        h.Grants.Write.Add("human");
        var info = await h.OpenAsync(Harness.Owner);
        var fake = new TerminalCaller("agent-2", IsOwner: true, IsAgent: true);
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.ReleaseControlAsync(fake, info.TerminalId, Harness.Ct)).Refusal);
    }

    [Fact]
    public async Task Releasing_when_nobody_holds_control_is_accepted_and_not_audited()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
        Assert.True((await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct)).Ok);
        Assert.Single(h.Audit.Kinds, k => k == "control.release");
    }

    [Fact]
    public async Task Control_on_an_unknown_terminal_is_NotFound()
    {
        await using var h = new Harness();
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.TakeControlAsync(Harness.Owner, "nope", Harness.Ct)).Refusal);
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.ReleaseControlAsync(Harness.Owner, "nope", Harness.Ct)).Refusal);
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.WriteAsync(Harness.Owner, "nope", new byte[1], Harness.Ct)).Refusal);
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.ResizeAsync(Harness.Owner, "nope", 1, 1, Harness.Ct)).Refusal);
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.CloseAsync(Harness.Owner, "nope", Harness.Ct)).Refusal);
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.SubscribeAsync(Harness.Owner, "nope", 0, Harness.Ct)).Result.Refusal);
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.WriteAsync(Harness.Owner, "", new byte[1], Harness.Ct)).Refusal);
    }

    [Fact]
    public async Task Exactly_one_of_many_simultaneous_takers_wins()
    {
        await using var h = new Harness(policy: new AllowAllPolicy());
        var info = await h.OpenAsync();
        await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);

        const int takers = 64;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, takers).Select(i => Task.Run(
            async () =>
            {
                await start.Task;
                return await h.Broker.TakeControlAsync(new TerminalCaller("m" + i, false, false), info.TerminalId, Harness.Ct);
            },
            Harness.Ct)).ToList();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(takers - 1, results.Count(r => r.Refusal == TerminalRefusal.WriterHeld));
        Assert.Single(h.Audit.Kinds, k => k == "control.take");
    }

    [Fact]
    public async Task Writer_role_is_exclusive_under_a_take_release_storm()
    {
        await using var h = new Harness(policy: new AllowAllPolicy());
        var info = await h.OpenAsync();
        await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);

        var holders = 0;
        var violations = 0;
        var acquisitions = 0;
        await Task.WhenAll(Enumerable.Range(0, 48).Select(i => Task.Run(
            async () =>
            {
                var me = new TerminalCaller("m" + i, false, false);
                for (var n = 0; n < 40; n++)
                {
                    var take = await h.Broker.TakeControlAsync(me, info.TerminalId, Harness.Ct);
                    if (!take.Ok)
                    {
                        await Task.Yield();
                        continue;
                    }

                    Interlocked.Increment(ref acquisitions);
                    if (Interlocked.Increment(ref holders) != 1)
                    {
                        Interlocked.Increment(ref violations);
                    }

                    // While holding, only I may write; everyone else must be refused.
                    var mine = await h.Broker.WriteAsync(me, info.TerminalId, new byte[] { 1 }, Harness.Ct);
                    var theirs = await h.Broker.WriteAsync(new TerminalCaller("m" + ((i + 1) % 48), false, false), info.TerminalId, new byte[] { 2 }, Harness.Ct);
                    if (!mine.Ok || theirs.Ok)
                    {
                        Interlocked.Increment(ref violations);
                    }

                    Interlocked.Decrement(ref holders);
                    var release = await h.Broker.ReleaseControlAsync(me, info.TerminalId, Harness.Ct);
                    if (!release.Ok)
                    {
                        Interlocked.Increment(ref violations);
                    }
                }
            },
            Harness.Ct)));

        Assert.Equal(0, violations);
        Assert.True(acquisitions > 0);

        // Only bytes from legitimate holders reached the guest.
        Assert.All(h.Channel.Last.AllWritten, b => Assert.Equal(1, b));
        Assert.Equal(acquisitions, h.Channel.Last.AllWritten.Length);
    }

    [Fact]
    public async Task Write_requires_the_writer_role_even_for_a_member_with_a_write_grant()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            var result = await h.Broker.WriteAsync(Harness.Human, info.TerminalId, new byte[] { 1 }, Harness.Ct);
            Assert.Equal(TerminalRefusal.Forbidden, result.Refusal);
            Assert.Empty(h.Channel.Last.Writes);
        }
    }

    [Fact]
    public async Task Write_requires_policy_permission_even_for_the_writer()
    {
        var policy = new TogglePolicy();
        await using var h = new Harness(policy: policy);
        var info = await h.OpenAsync();
        Assert.True((await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[] { 1 }, Harness.Ct)).Ok);
        policy.AllowWrite = false;
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[] { 2 }, Harness.Ct)).Refusal);
        Assert.Equal([1], h.Channel.Last.AllWritten.Select(b => (int)b));
    }

    [Fact]
    public async Task A_throwing_policy_denies()
    {
        await using var h = new Harness(policy: new ThrowingPolicy());
        var (result, _) = await h.Broker.OpenAsync(Harness.Owner, "agent-1", "vm", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.Forbidden, result.Refusal);
    }

    [Fact]
    public async Task Agent_input_reaches_the_guest_and_is_not_audited_as_human()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        Assert.True((await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, "ls\n"u8.ToArray(), Harness.Ct)).Ok);
        Assert.Equal("ls\n", System.Text.Encoding.UTF8.GetString(h.Channel.Last.AllWritten));
        Assert.DoesNotContain("input.human", h.Audit.Kinds);
    }

    [Fact]
    public async Task Human_input_is_audited_once_per_control_stint_by_default()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
            await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
            for (var i = 0; i < 5; i++)
            {
                Assert.True((await h.Broker.WriteAsync(Harness.Human, info.TerminalId, new byte[] { 65 }, Harness.Ct)).Ok);
            }

            Assert.Single(h.Audit.Kinds, k => k == "input.human");

            await h.Broker.ReleaseControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
            await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
            await h.Broker.WriteAsync(Harness.Human, info.TerminalId, new byte[] { 65 }, Harness.Ct);
            Assert.Equal(2, h.Audit.Kinds.Count(k => k == "input.human"));
        }
    }

    [Fact]
    public async Task Human_input_can_be_audited_on_every_write()
    {
        var (h, info) = await WithHumanWriterAsync(new TerminalOptions { AuditEveryHumanInput = true });
        await using (h)
        {
            await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
            await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
            for (var i = 0; i < 3; i++)
            {
                await h.Broker.WriteAsync(Harness.Human, info.TerminalId, new byte[] { 65 }, Harness.Ct);
            }

            Assert.Equal(3, h.Audit.Kinds.Count(k => k == "input.human"));
        }
    }

    [Fact]
    public async Task Human_input_is_refused_when_the_audit_row_cannot_be_written()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
            await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
            h.Audit.Fail = true;
            var result = await h.Broker.WriteAsync(Harness.Human, info.TerminalId, new byte[] { 65 }, Harness.Ct);
            Assert.Equal(TerminalRefusal.Forbidden, result.Refusal);
            Assert.Empty(h.Channel.Last.Writes);
        }
    }

    [Fact]
    public async Task Large_writes_are_split_into_16_KiB_guest_writes_in_order()
    {
        await using var h = new Harness(new TerminalOptions { InputBytesPerSecond = 1024 * 1024 });
        var info = await h.OpenAsync();
        var data = Enumerable.Range(0, 40_000).Select(i => (byte)(i % 251)).ToArray();
        Assert.True((await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, data, Harness.Ct)).Ok);
        Assert.Equal([16384, 16384, 7232], h.Channel.Last.Writes.Select(w => w.Length));
        Assert.Equal(data, h.Channel.Last.AllWritten);
    }

    [Fact]
    public async Task Empty_input_is_accepted_and_sends_nothing()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        Assert.True((await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, ReadOnlyMemory<byte>.Empty, Harness.Ct)).Ok);
        Assert.Empty(h.Channel.Last.Writes);
    }

    [Fact]
    public async Task Input_over_64_KiB_per_second_is_rate_limited_until_the_clock_moves()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        var chunk = new byte[16 * 1024];
        for (var i = 0; i < 4; i++)
        {
            Assert.True((await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, chunk, Harness.Ct)).Ok);
        }

        var limited = await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[1], Harness.Ct);
        Assert.Equal(TerminalRefusal.RateLimited, limited.Refusal);

        h.Time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True((await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, chunk, Harness.Ct)).Ok);
        Assert.Equal(TerminalRefusal.RateLimited, (await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[1], Harness.Ct)).Refusal);

        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[64 * 1024], Harness.Ct)).Ok);
    }

    [Fact]
    public async Task A_write_larger_than_the_whole_burst_is_always_rate_limited_and_sends_nothing()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        var result = await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[64 * 1024 + 1], Harness.Ct);
        Assert.Equal(TerminalRefusal.RateLimited, result.Refusal);
        Assert.Empty(h.Channel.Last.Writes);
    }

    [Fact]
    public async Task Rate_limit_is_per_client()
    {
        await using var h = new Harness(policy: new AllowAllPolicy());
        var info = await h.OpenAsync();
        Assert.True((await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[64 * 1024], Harness.Ct)).Ok);
        Assert.Equal(TerminalRefusal.RateLimited, (await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[1], Harness.Ct)).Refusal);

        await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
        h.Audit.Fail = false;
        await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
        Assert.True((await h.Broker.WriteAsync(Harness.Human, info.TerminalId, new byte[64 * 1024], Harness.Ct)).Ok);
    }

    [Fact]
    public async Task Guest_write_failure_is_reported_not_thrown()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        h.Channel.Last.FailWrites = true;
        var result = await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[] { 1 }, Harness.Ct);
        Assert.Equal(TerminalRefusal.NoBackend, result.Refusal);
    }

    [Fact]
    public async Task Resize_needs_the_writer_and_a_valid_size_and_reaches_the_guest()
    {
        var (h, info) = await WithHumanWriterAsync();
        await using (h)
        {
            Assert.Equal(TerminalRefusal.InvalidSize, (await h.Broker.ResizeAsync(Harness.Agent1, info.TerminalId, 0, 10, Harness.Ct)).Refusal);
            Assert.Equal(TerminalRefusal.InvalidSize, (await h.Broker.ResizeAsync(Harness.Agent1, info.TerminalId, 10, 1001, Harness.Ct)).Refusal);
            Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.ResizeAsync(Harness.Human, info.TerminalId, 100, 30, Harness.Ct)).Refusal);
            Assert.True((await h.Broker.ResizeAsync(Harness.Agent1, info.TerminalId, 120, 40, Harness.Ct)).Ok);
            Assert.Equal([(120, 40)], h.Channel.Last.Resizes);
            var listed = h.Broker.List(Harness.Owner)[0];
            Assert.Equal((120, 40), (listed.Cols, listed.Rows));
        }
    }

    [Fact]
    public async Task Concurrent_writes_from_the_writer_do_not_interleave_inside_a_split_write()
    {
        await using var h = new Harness(new TerminalOptions { InputBytesPerSecond = 100 * 1024 * 1024 });
        var info = await h.OpenAsync();
        var a = Enumerable.Repeat((byte)'a', 40_000).ToArray();
        var b = Enumerable.Repeat((byte)'b', 40_000).ToArray();
        await Task.WhenAll(
            Task.Run(() => h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, a, Harness.Ct), Harness.Ct),
            Task.Run(() => h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, b, Harness.Ct), Harness.Ct));
        var all = h.Channel.Last.AllWritten;
        Assert.Equal(80_000, all.Length);
        Assert.Equal(1, all.Zip(all.Skip(1), (x, y) => x != y).Count(d => d));
    }

    private sealed class TogglePolicy : ITerminalAccessPolicy
    {
        public bool AllowWrite { get; set; } = true;

        public bool CanView(TerminalCaller caller, TerminalInfo terminal) => true;

        public bool CanWrite(TerminalCaller caller, TerminalInfo terminal) => AllowWrite;
    }

    private sealed class ThrowingPolicy : ITerminalAccessPolicy
    {
        public bool CanView(TerminalCaller caller, TerminalInfo terminal) => throw new InvalidOperationException();

        public bool CanWrite(TerminalCaller caller, TerminalInfo terminal) => throw new InvalidOperationException();
    }
}
