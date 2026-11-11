using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Mentions;
using Bazaar.Server.Gc.Write;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.Turns.Posting;

/// <summary>The GC-backed agent post sink over a recording fake of <see cref="IGcLogWriter"/>.</summary>
public sealed class GcAgentPostSinkTests
{
    private static readonly Guid _agentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _gcId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid _turnId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid _owner = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid _ana = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid _ben = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid _carl = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [Fact]
    public async Task A_post_reaches_the_log_writer_once_with_the_agent_author_gc_and_text()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard());

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Here is my answer.", TestContext.Current.CancellationToken);

        var request = Assert.Single(log.Requests);
        Assert.Equal(_gcId, request.GcId);
        Assert.Equal(GcEventKinds.Message, request.Kind);
        Assert.Equal(AuthorKind.Agent, request.AuthorKind);
        Assert.Equal(_agentId, request.AuthorId);
        Assert.Equal(SourceTrust.Agent, request.SourceTrust);
        Assert.Null(request.SessionId);
        Assert.Empty(request.Mentions);
        Assert.Equal(GcAgentPostSink.ClientMsgId(_turnId, "Here is my answer."), request.ClientMsgId);
    }

    [Fact]
    public async Task The_body_is_sealed_before_it_reaches_the_writer()
    {
        var log = new RecordingGcLogWriter();
        var codec = new PlaintextGcBodyCodec();
        var sink = new GcAgentPostSink(log, codec, new ScriptedLoopGuard());

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Sealed text.", TestContext.Current.CancellationToken);

        var body = Assert.Single(log.Requests).Message;
        Assert.NotNull(body);
        Assert.Equal(PlaintextGcBodyCodec.PlaintextKeyId, body.KeyId);
        Assert.Equal("Sealed text.", codec.Open(body.BodyCiphertext, body.KeyId, body.Nonce));
    }

    [Fact]
    public async Task A_retry_of_the_same_turn_and_text_uses_the_same_client_message_id_and_is_a_duplicate()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard());

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Same text.", TestContext.Current.CancellationToken);
        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Same text.", TestContext.Current.CancellationToken);

        Assert.Equal(2, log.Requests.Count);
        Assert.Equal(log.Requests[0].ClientMsgId, log.Requests[1].ClientMsgId);
        Assert.Equal(1, log.AcceptedCount);
        Assert.Equal(1, log.DuplicateCount);
    }

    [Fact]
    public async Task A_different_text_in_the_same_turn_is_not_swallowed_as_a_duplicate()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard());

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "First.", TestContext.Current.CancellationToken);
        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Second.", TestContext.Current.CancellationToken);

        Assert.NotEqual(log.Requests[0].ClientMsgId, log.Requests[1].ClientMsgId);
        Assert.Equal(2, log.AcceptedCount);
        Assert.Equal(0, log.DuplicateCount);
    }

    [Fact]
    public void The_client_message_id_stays_within_the_writer_limit()
    {
        var id = GcAgentPostSink.ClientMsgId(_turnId, new string('x', 10_000));

        Assert.True(id.Length <= AppendRequest.MaxClientMsgIdLength);
        Assert.StartsWith(GcAgentPostSink.ClientMsgIdPrefix, id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_to_a_malformed_gc_id_is_refused_before_it_reaches_the_writer()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sink.PostAsync(_agentId, _turnId, "not-a-guid", "Text.", TestContext.Current.CancellationToken));
        Assert.Empty(log.Requests);
    }

    [Fact]
    public async Task An_empty_post_is_refused_before_it_reaches_the_writer()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "   ", TestContext.Current.CancellationToken));
        Assert.Empty(log.Requests);
    }

    [Fact]
    public async Task A_tripped_guard_refuses_the_post_and_appends_nothing()
    {
        var log = new RecordingGcLogWriter();
        var guard = new ScriptedLoopGuard(GuardVerdict.Trip(GuardKind.PingPong, "Two agents alternated.", [_agentId.ToString("D")]));
        var directory = new FixedDirectory(new GcMentionRoster(_owner, [new GcMentionMember(_ana, "ana")]));
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), guard, directory, new RecordingInboxWriter());

        var refused = await Assert.ThrowsAsync<AgentPostRefusedException>(() =>
            sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Hi @ana", TestContext.Current.CancellationToken));

        Assert.Equal(GuardKind.PingPong, refused.GuardKind);
        Assert.Equal("Two agents alternated.", refused.Reason);
        Assert.Empty(log.Requests);
        Assert.Equal(0, directory.Loads);
    }

    [Fact]
    public async Task The_guard_sees_the_post_as_an_agent_message_in_the_gc_at_depth_zero()
    {
        var guard = new ScriptedLoopGuard();
        var sink = new GcAgentPostSink(new RecordingGcLogWriter(), new PlaintextGcBodyCodec(), guard);

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Text.", TestContext.Current.CancellationToken);

        var seen = Assert.Single(guard.Events);
        Assert.Equal(new AgentMessage(_gcId, _agentId.ToString("D"), 0), seen);
    }

    [Fact]
    public async Task Accepted_mentions_reach_the_append_request_and_write_one_inbox_row_each()
    {
        var log = new RecordingGcLogWriter();
        var inbox = new RecordingInboxWriter();
        var directory = new FixedDirectory(new GcMentionRoster(_owner, [new GcMentionMember(_ana, "ana"), new GcMentionMember(_ben, "ben")]));
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard(), directory, inbox);

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Hi @ana, ping @ben and @Ana.", TestContext.Current.CancellationToken);

        var request = Assert.Single(log.Requests);
        Assert.Equal(new[] { _ana, _ben }, request.Mentions);
        var row = Assert.Single(inbox.Written, r => r.PrincipalId == _ana);
        Assert.Equal((_gcId, 1L, InboxReason.Mention), (row.GcId, row.Seq, row.Reason));
        Assert.Contains(inbox.Written, r => r.PrincipalId == _ben && r.Seq == 1L);
        Assert.Equal(2, inbox.Written.Count);
    }

    [Fact]
    public async Task A_mention_of_a_name_that_is_not_an_active_member_is_plain_text()
    {
        var log = new RecordingGcLogWriter();
        var inbox = new RecordingInboxWriter();
        var directory = new FixedDirectory(new GcMentionRoster(_owner, [new GcMentionMember(_ana, "ana")]));
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard(), directory, inbox);

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Cc @carl and @ana.", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { _ana }, Assert.Single(log.Requests).Mentions);
        Assert.DoesNotContain(inbox.Written, r => r.PrincipalId == _carl);
    }

    [Fact]
    public async Task A_self_mention_is_kept_on_the_message_but_writes_no_inbox_row()
    {
        var log = new RecordingGcLogWriter();
        var inbox = new RecordingInboxWriter();
        var directory = new FixedDirectory(new GcMentionRoster(_owner, [new GcMentionMember(_agentId, "me")]));
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard(), directory, inbox);

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Note to @me.", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { _agentId }, Assert.Single(log.Requests).Mentions);
        Assert.Empty(inbox.Written);
    }

    [Fact]
    public async Task Without_a_directory_the_post_carries_no_mentions_and_writes_no_inbox_rows()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard());

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Hi @ana.", TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(log.Requests).Mentions);
    }

    [Fact]
    public async Task A_retry_with_mentions_is_a_duplicate_and_writes_the_same_inbox_rows_again()
    {
        var log = new RecordingGcLogWriter();
        var inbox = new RecordingInboxWriter();
        var directory = new FixedDirectory(new GcMentionRoster(_owner, [new GcMentionMember(_ana, "ana")]));
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec(), new ScriptedLoopGuard(), directory, inbox);

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Hi @ana.", TestContext.Current.CancellationToken);
        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Hi @ana.", TestContext.Current.CancellationToken);

        Assert.Equal(1, log.AcceptedCount);
        Assert.Equal(1, log.DuplicateCount);
        Assert.Equal(2, inbox.Written.Count);
        Assert.All(inbox.Written, r => Assert.Equal((_ana, 1L), (r.PrincipalId, r.Seq)));
    }

    [Fact]
    public void A_directory_without_an_inbox_writer_is_refused_at_construction()
    {
        var directory = new FixedDirectory(new GcMentionRoster(_owner, []));

        Assert.Throws<ArgumentException>(() =>
            new GcAgentPostSink(new RecordingGcLogWriter(), new PlaintextGcBodyCodec(), new ScriptedLoopGuard(), directory));
    }

    [Fact]
    public void A_sink_without_a_loop_guard_cannot_be_built()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GcAgentPostSink(new RecordingGcLogWriter(), new PlaintextGcBodyCodec(), null!));
    }

    [Fact]
    public void The_registration_provides_the_sink_and_keeps_an_existing_one()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGcLogWriter>(new RecordingGcLogWriter());
        services.AddSingleton<IGcBodyCodec, PlaintextGcBodyCodec>();
        services.AddSingleton<ILoopGuard>(new LoopGuardFacade(TimeProvider.System));
        services.AddGcAgentPostSink();
        services.AddGcAgentPostSink();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<GcAgentPostSink>(provider.GetRequiredService<IAgentPostSink>());
        Assert.Single(services, d => d.ServiceType == typeof(IAgentPostSink));
    }

    [Fact]
    public void Without_a_loop_guard_the_registered_sink_cannot_be_resolved()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGcLogWriter>(new RecordingGcLogWriter());
        services.AddSingleton<IGcBodyCodec, PlaintextGcBodyCodec>();
        services.AddGcAgentPostSink();

        using var provider = services.BuildServiceProvider();
        Assert.ThrowsAny<InvalidOperationException>(() => provider.GetRequiredService<IAgentPostSink>());
    }

    /// <summary>Records every append and answers a repeated (author, client message id) with the original, like the real writer.</summary>
    private sealed class RecordingGcLogWriter : IGcLogWriter
    {
        private readonly Dictionary<(Guid Author, string ClientMsgId), AppendResult> _seen = [];
        private long _seq;

        public List<AppendRequest> Requests { get; } = [];

        public int AcceptedCount { get; private set; }

        public int DuplicateCount { get; private set; }

        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (_seen.TryGetValue((request.AuthorId, request.ClientMsgId), out var original))
            {
                DuplicateCount++;
                return Task.FromResult(original with { Duplicate = true });
            }

            AcceptedCount++;
            var result = new AppendResult(request.GcId, ++_seq, Guid.NewGuid(), Duplicate: false, DateTimeOffset.UnixEpoch);
            _seen[(request.AuthorId, request.ClientMsgId)] = result;
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException("The sink appends one event at a time.");
    }

    /// <summary>Returns one fixed verdict and records each event.</summary>
    private sealed class ScriptedLoopGuard(GuardVerdict? verdict = null) : ILoopGuard
    {
        public List<LoopEvent> Events { get; } = [];

        public GuardVerdict Check(LoopEvent loopEvent)
        {
            Events.Add(loopEvent);
            return verdict ?? GuardVerdict.Ok;
        }

        public bool ScoredWakeWithinCap(string agentId) => true;
    }

    /// <summary>Returns a fixed roster and counts the loads.</summary>
    private sealed class FixedDirectory(GcMentionRoster roster) : IGcMentionDirectory
    {
        public int Loads { get; private set; }

        public Task<GcMentionRoster> LoadAsync(Guid gcId, CancellationToken ct)
        {
            Loads++;
            return Task.FromResult(roster);
        }
    }

    /// <summary>Records every inbox row written.</summary>
    private sealed class RecordingInboxWriter : IInboxRowWriter
    {
        public List<InboxRow> Written { get; } = [];

        public Task<int> WriteAsync(IReadOnlyList<InboxRow> rows, CancellationToken ct)
        {
            Written.AddRange(rows);
            return Task.FromResult(rows.Count);
        }

        public Task<int> WriteAsync(NpgsqlTransaction tx, IReadOnlyList<InboxRow> rows, CancellationToken ct) =>
            throw new NotSupportedException("The sink writes inbox rows outside the append transaction.");
    }
}
