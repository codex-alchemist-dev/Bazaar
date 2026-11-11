using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Agents.Turns.Posting;

/// <summary>The GC-backed agent post sink over a recording fake of <see cref="IGcLogWriter"/>.</summary>
public sealed class GcAgentPostSinkTests
{
    private static readonly Guid _agentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _gcId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid _turnId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task A_post_reaches_the_log_writer_once_with_the_agent_author_gc_and_text()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec());

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
        var sink = new GcAgentPostSink(log, codec);

        await sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "Sealed text.", TestContext.Current.CancellationToken);

        var body = Assert.Single(log.Requests).Message;
        Assert.NotNull(body);
        Assert.Equal(PlaintextGcBodyCodec.PlaintextKeyId, body.KeyId);
        Assert.Equal("Sealed text.", await codec.OpenAsync(body.BodyCiphertext, body.KeyId, body.Nonce, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_retry_of_the_same_turn_and_text_uses_the_same_client_message_id_and_is_a_duplicate()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec());

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
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec());

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
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sink.PostAsync(_agentId, _turnId, "not-a-guid", "Text.", TestContext.Current.CancellationToken));
        Assert.Empty(log.Requests);
    }

    [Fact]
    public async Task An_empty_post_is_refused_before_it_reaches_the_writer()
    {
        var log = new RecordingGcLogWriter();
        var sink = new GcAgentPostSink(log, new PlaintextGcBodyCodec());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sink.PostAsync(_agentId, _turnId, _gcId.ToString("D"), "   ", TestContext.Current.CancellationToken));
        Assert.Empty(log.Requests);
    }

    [Fact]
    public void The_registration_provides_the_sink_and_keeps_an_existing_one()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGcLogWriter>(new RecordingGcLogWriter());
        services.AddSingleton<IGcBodyCodec, PlaintextGcBodyCodec>();
        services.AddGcAgentPostSink();
        services.AddGcAgentPostSink();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<GcAgentPostSink>(provider.GetRequiredService<IAgentPostSink>());
        Assert.Single(services, d => d.ServiceType == typeof(IAgentPostSink));
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
}
