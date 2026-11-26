using System.Text;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Interrupts;
using Bazaar.Server.Setup.FirstRun;
using Bazaar.Testing;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Interrupts.Tests;

public sealed class InterruptStackTests(InterruptDb db) : IClassFixture<InterruptDb>
{
    private InterruptDb Db => db;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid NewGuid() => Guid.CreateVersion7();

    private static readonly Guid _author = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");
    private static readonly Guid _companyOwner = Guid.Parse("aaaaaaaa-0000-7000-8000-000000000001");
    private static readonly FakeBodyCodec _codec = new();
    private static readonly IInterruptReasonCipher _testCipher = new GcBodyInterruptReasonCipher(_codec);

    /// <summary>Each test gets its own clock and stack over the shared class database; agents are fresh per test.</summary>
    private static (InterruptStackService Stack, FakeClock Clock) NewStack(NpgsqlDataSource dataSource)
    {
        var clock = new FakeClock();
        return (new InterruptStackService(dataSource, _testCipher, new FakeOwner(), clock), clock);
    }

    [Fact]
    public async Task The_emergency_tag_is_stored_for_an_owner_frame_and_dropped_for_a_system_frame()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var gc = NewGuid();
        var ownerAgent = NewGuid();
        var systemAgent = NewGuid();
        var plainAgent = NewGuid();

        var tagged = await stack.PushAsync(ownerAgent, _author, InterruptSource.Owner, "Now", gc, emergency: true, ct: Ct);
        var system = await stack.PushAsync(systemAgent, _author, InterruptSource.SystemBuild, "Build", gc, emergency: true, ct: Ct);
        var plain = await stack.PushAsync(plainAgent, _author, InterruptSource.Owner, "Later", gc, ct: Ct);

        Assert.True(tagged.Emergency);
        Assert.False(system.Emergency);
        Assert.False(plain.Emergency);
        Assert.True(Assert.Single(await stack.GetStackAsync(ownerAgent, Ct)).Emergency);
        Assert.False(Assert.Single(await stack.GetStackAsync(systemAgent, Ct)).Emergency);
        Assert.False(Assert.Single(await stack.GetStackAsync(plainAgent, Ct)).Emergency);
    }

    [Fact]
    public async Task A_first_push_makes_an_active_root_frame()
    {
        SkipWithoutDatabase();
        var (stack, clock) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();

        var frame = await stack.PushAsync(agent, _author, InterruptSource.Owner, "Stop and review", gc, ct: Ct);

        Assert.Equal(InterruptFrameState.Active, frame.State);
        Assert.Null(frame.ParentFrameId);
        Assert.Equal(clock.GetUtcNow(), frame.PushedAt);
        Assert.Equal(1, frame.Version);
        var live = await stack.GetStackAsync(agent, Ct);
        Assert.Equal(frame.Id, Assert.Single(live).Id);
    }

    [Fact]
    public async Task A_second_push_suspends_the_top_and_links_it_as_parent()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, _author, InterruptSource.Owner, "First", gc, ct: Ct);

        var second = await stack.PushAsync(agent, _author, InterruptSource.SystemBuild, "Build broke", gc, ct: Ct);

        Assert.Equal(first.Id, second.ParentFrameId);
        var live = await stack.GetStackAsync(agent, Ct);
        Assert.Equal([second.Id, first.Id], live.Select(f => f.Id));
        Assert.Equal(InterruptFrameState.Active, live[0].State);
        Assert.Equal(InterruptFrameState.Suspended, live[1].State);
        Assert.Equal(2, live[1].Version);
    }

    [Fact]
    public async Task A_third_push_is_refused_and_the_stack_is_unchanged()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        await stack.PushAsync(agent, _author, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await stack.PushAsync(agent, _author, InterruptSource.Owner, "Second", gc, ct: Ct);
        var before = await stack.GetStackAsync(agent, Ct);

        var error = await Assert.ThrowsAsync<InterruptDepthExceededException>(
            () => stack.PushAsync(agent, _author, InterruptSource.Owner, "Third", gc, ct: Ct));

        Assert.Equal(agent, error.AgentId);
        var after = await stack.GetStackAsync(agent, Ct);
        Assert.Equal(before.Select(f => (f.Id, f.State, f.Version)), after.Select(f => (f.Id, f.State, f.Version)));
        Assert.Equal(second.Id, after[0].Id);
    }

    [Fact]
    public async Task Pop_ends_the_top_and_resumes_its_parent()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, _author, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await stack.PushAsync(agent, _author, InterruptSource.Owner, "Second", gc, ct: Ct);

        var resumed = await stack.PopAsync(agent, second.Id, Ct);

        Assert.NotNull(resumed);
        Assert.Equal(first.Id, resumed.Id);
        Assert.Equal(InterruptFrameState.Active, resumed.State);
        var live = await stack.GetStackAsync(agent, Ct);
        Assert.Equal(first.Id, Assert.Single(live).Id);
    }

    [Fact]
    public async Task Popping_the_last_frame_leaves_an_empty_stack()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var frame = await stack.PushAsync(agent, _author, InterruptSource.Owner, "Only", NewGuid(), ct: Ct);

        var resumed = await stack.PopAsync(agent, frame.Id, Ct);

        Assert.Null(resumed);
        Assert.Empty(await stack.GetStackAsync(agent, Ct));
    }

    [Fact]
    public async Task Pop_is_idempotent_by_frame_id()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, _author, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await stack.PushAsync(agent, _author, InterruptSource.Owner, "Second", gc, ct: Ct);
        await stack.PopAsync(agent, second.Id, Ct);
        var afterFirstPop = await stack.GetStackAsync(agent, Ct);

        var repeat = await stack.PopAsync(agent, second.Id, Ct);

        Assert.Null(repeat);
        Assert.Equal(afterFirstPop.Select(f => (f.Id, f.Version)), (await stack.GetStackAsync(agent, Ct)).Select(f => (f.Id, f.Version)));
        Assert.Equal(first.Id, Assert.Single(await stack.GetStackAsync(agent, Ct)).Id);
    }

    [Fact]
    public async Task Popping_a_suspended_frame_is_refused()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, _author, InterruptSource.Owner, "First", gc, ct: Ct);
        await stack.PushAsync(agent, _author, InterruptSource.Owner, "Second", gc, ct: Ct);

        await Assert.ThrowsAsync<InterruptFrameNotTopException>(() => stack.PopAsync(agent, first.Id, Ct));

        Assert.Equal(2, (await stack.GetStackAsync(agent, Ct)).Count);
    }

    [Fact]
    public async Task Popping_a_frame_of_another_agent_is_refused()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var owner = NewGuid();
        var other = NewGuid();
        var frame = await stack.PushAsync(owner, _author, InterruptSource.Owner, "Mine", NewGuid(), ct: Ct);

        await Assert.ThrowsAsync<InterruptStackException>(() => stack.PopAsync(other, frame.Id, Ct));

        Assert.Equal(frame.Id, Assert.Single(await stack.GetStackAsync(owner, Ct)).Id);
    }

    [Fact]
    public async Task Identical_system_pushes_within_60_seconds_collapse_into_one_frame()
    {
        SkipWithoutDatabase();
        var (stack, clock) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, _author, InterruptSource.SystemBuild, "CI red", gc, ct: Ct);
        clock.Advance(TimeSpan.FromSeconds(59));

        var repeat = await stack.PushAsync(agent, _author, InterruptSource.SystemBuild, "CI red", gc, ct: Ct);

        Assert.Equal(first.Id, repeat.Id);
        Assert.Single(await stack.GetStackAsync(agent, Ct));
    }

    [Fact]
    public async Task An_identical_system_push_after_the_window_is_a_new_frame()
    {
        SkipWithoutDatabase();
        var (stack, clock) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        await stack.PushAsync(agent, _author, InterruptSource.SystemBuild, "CI red", gc, ct: Ct);
        clock.Advance(InterruptStackService.SystemDedupeWindow + TimeSpan.FromSeconds(1));

        var later = await stack.PushAsync(agent, _author, InterruptSource.SystemBuild, "CI red", gc, ct: Ct);

        Assert.Equal(2, (await stack.GetStackAsync(agent, Ct)).Count);
        Assert.NotNull(later.ParentFrameId);
    }

    [Fact]
    public async Task Owner_pushes_are_never_collapsed()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        await stack.PushAsync(agent, _author, InterruptSource.Owner, "Same words", gc, ct: Ct);

        var second = await stack.PushAsync(agent, _author, InterruptSource.Owner, "Same words", gc, ct: Ct);

        Assert.Equal(2, (await stack.GetStackAsync(agent, Ct)).Count);
        Assert.NotNull(second.ParentFrameId);
    }

    [Fact]
    public async Task The_stack_is_rebuilt_from_the_table_after_a_restart()
    {
        SkipWithoutDatabase();
        var agent = NewGuid();
        var gc = NewGuid();
        var (before, _) = NewStack(db.DataSource);
        var first = await before.PushAsync(agent, _author, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await before.PushAsync(agent, _author, InterruptSource.SystemSecurity, "Alert", gc, ct: Ct);

        // A new service instance over the same database stands in for a restarted host.
        var (after, _) = NewStack(db.DataSource);
        var live = await after.GetStackAsync(agent, Ct);

        Assert.Equal([second.Id, first.Id], live.Select(f => f.Id));
        var resumed = await after.PopAsync(agent, second.Id, Ct);
        Assert.Equal(first.Id, resumed!.Id);
    }

    [Fact]
    public async Task Concurrent_pushes_for_one_agent_never_exceed_depth_two()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try
            {
                await stack.PushAsync(agent, _author, InterruptSource.Owner, $"Push {i}", gc, ct: Ct);
                return true;
            }
            catch (InterruptDepthExceededException)
            {
                return false;
            }
        }));

        Assert.Equal(2, outcomes.Count(ok => ok));
        Assert.Equal(2, (await stack.GetStackAsync(agent, Ct)).Count);
    }

    [Fact]
    public async Task Stacks_of_different_agents_are_independent()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var gc = NewGuid();
        var a = NewGuid();
        var b = NewGuid();
        await stack.PushAsync(a, _author, InterruptSource.Owner, "A1", gc, ct: Ct);
        await stack.PushAsync(a, _author, InterruptSource.Owner, "A2", gc, ct: Ct);

        var bFrame = await stack.PushAsync(b, _author, InterruptSource.Owner, "B1", gc, ct: Ct);

        Assert.Null(bFrame.ParentFrameId);
        Assert.Equal(2, (await stack.GetStackAsync(a, Ct)).Count);
    }

    [Fact]
    public async Task The_database_refuses_a_second_active_frame_for_one_agent()
    {
        SkipWithoutDatabase();
        var agent = NewGuid();
        var gc = NewGuid();
        await using (var first = db.DataSource.CreateCommand(_insertActive))
        {
            first.Parameters.AddWithValue("id", NewGuid());
            first.Parameters.AddWithValue("agent_id", agent);
            first.Parameters.AddWithValue("key_id", _author);
            first.Parameters.AddWithValue("gc_id", gc);
            await first.ExecuteNonQueryAsync(Ct);
        }

        await using var second = db.DataSource.CreateCommand(_insertActive);
        second.Parameters.AddWithValue("id", NewGuid());
        second.Parameters.AddWithValue("agent_id", agent);
        second.Parameters.AddWithValue("key_id", _author);
        second.Parameters.AddWithValue("gc_id", gc);
        await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task A_reason_is_stored_sealed_and_reads_back_in_plain_form()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        const string reason = "payment-service build red on main";

        var frame = await stack.PushAsync(agent, _author, InterruptSource.SystemBuild, reason, NewGuid(), ct: Ct);

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT reason_sealed, reason_key_id FROM interrupt_frame WHERE id = @id");
        cmd.Parameters.AddWithValue("id", frame.Id);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var stored = (byte[])reader.GetValue(0);
        Assert.Equal(FakeBodyCodec.KeyFor(_companyOwner), reader.GetGuid(1));
        Assert.DoesNotContain("payment-service", Encoding.UTF8.GetString(stored));
        Assert.Equal(reason, (await stack.GetStackAsync(agent, Ct)).Single().Reason);
    }

    [Fact]
    public async Task The_plaintext_reason_column_is_gone()
    {
        SkipWithoutDatabase();
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'interrupt_frame' AND column_name = 'reason'");

        Assert.Equal(0L, await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task An_owner_push_is_sealed_under_the_pushers_key_and_a_system_push_under_the_owners()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, _author, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await stack.PushAsync(agent, _author, InterruptSource.SystemSecurity, "Alert", gc, ct: Ct);

        Assert.Equal(FakeBodyCodec.KeyFor(_author), await KeyIdOfAsync(first.Id));
        Assert.Equal(FakeBodyCodec.KeyFor(_companyOwner), await KeyIdOfAsync(second.Id));
    }

    [Fact]
    public async Task A_reason_whose_authors_key_was_erased_reads_as_erased_and_the_frame_stays()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var erased = Guid.CreateVersion7();
        var frame = await stack.PushAsync(agent, erased, InterruptSource.Owner, "private words", NewGuid(), ct: Ct);
        _codec.Erase(erased);

        var live = Assert.Single(await stack.GetStackAsync(agent, Ct));

        Assert.Equal(frame.Id, live.Id);
        Assert.Equal(Bazaar.Server.Gc.Body.GcBodyText.ErasedText, live.Reason);
    }

    [Fact]
    public async Task A_system_push_without_a_company_owner_is_refused()
    {
        SkipWithoutDatabase();
        var stack = new InterruptStackService(db.DataSource, _testCipher, new FakeOwner(null), new FakeClock());

        await Assert.ThrowsAsync<InterruptStackException>(
            () => stack.PushAsync(NewGuid(), _author, InterruptSource.SystemBuild, "CI red", NewGuid(), ct: Ct));
    }

    private async Task<Guid> KeyIdOfAsync(Guid frameId)
    {
        await using var cmd = Db.DataSource.CreateCommand("SELECT reason_key_id FROM interrupt_frame WHERE id = @id");
        cmd.Parameters.AddWithValue("id", frameId);
        return (Guid)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_reason_longer_than_500_characters_is_refused()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);

        await Assert.ThrowsAsync<ArgumentException>(
            () => stack.PushAsync(NewGuid(), _author, InterruptSource.Owner, new string('x', 501), NewGuid(), ct: Ct));
    }

    [Fact]
    public async Task An_empty_reason_is_refused_before_it_reaches_the_database()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => stack.PushAsync(NewGuid(), _author, InterruptSource.Owner, " ", NewGuid(), ct: Ct));
    }

    private const string _insertActive = """
        INSERT INTO interrupt_frame (id, agent_id, source, reason_sealed, reason_nonce, reason_key_id, gc_id, state, pushed_at, version)
        VALUES (@id, @agent_id, 'owner', decode(repeat('ab', 20), 'hex'), decode(repeat('cd', 12), 'hex'), @key_id, @gc_id, 'active', now(), 1)
        """;

    private sealed class FakeOwner(Guid? owner = null) : IFirstRunOwner
    {
        private readonly Guid? _owner = owner ?? _companyOwner;

        public Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(_owner);
    }
}

/// <summary>A reversible stand-in for the per-person body codec: one key id per author, erasable, 12-byte nonce, 16-byte tag.</summary>
internal sealed class FakeBodyCodec : IGcBodyCodec
{
    private readonly HashSet<Guid> _erased = [];

    public static Guid KeyFor(Guid author) => new(author.ToByteArray().Select(b => (byte)(b ^ 0x5A)).ToArray());

    public void Erase(Guid author) => _erased.Add(KeyFor(author));

    public ValueTask<MessageBody> SealAsync(Guid authorId, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text).Select(b => (byte)(b ^ 0x5A)).Concat(new byte[16]).ToArray();
        return ValueTask.FromResult(new MessageBody(bytes, KeyFor(authorId), new byte[12]));
    }

    public ValueTask<string?> OpenAsync(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce, CancellationToken ct)
    {
        if (_erased.Contains(keyId))
            return ValueTask.FromResult<string?>(null);
        var plain = ciphertext.ToArray().Take(ciphertext.Length - 16).Select(b => (byte)(b ^ 0x5A)).ToArray();
        return ValueTask.FromResult<string?>(Encoding.UTF8.GetString(plain));
    }
}
