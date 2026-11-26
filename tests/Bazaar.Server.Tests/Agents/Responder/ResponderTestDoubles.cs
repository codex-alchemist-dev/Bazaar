using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Tests.Agents.Responder;

/// <summary>
/// One in-memory GC that is both the responder's store and its log writer, so a reply the responder appends shows up in the
/// history it reads next. It keeps the append rules the responder relies on: a repeated (author, client message id) answers with
/// the original event, and an archived GC refuses writes.
/// </summary>
internal sealed class InMemoryResponderGc(Guid gcId) : IResponderGcStore, IGcLogWriter
{
    private readonly Lock _gate = new();
    private readonly List<ResponderMessage> _messages = [];
    private readonly Dictionary<(Guid Author, string Key), AppendResult> _keys = [];
    private readonly HashSet<Guid> _members = [];
    private long _head;

    public Guid GcId { get; } = gcId;

    public bool Archived { get; set; }

    /// <summary>Every message in order, including the agents' replies.</summary>
    public IReadOnlyList<ResponderMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    /// <summary>Makes a principal an active member.</summary>
    public void Join(Guid principal)
    {
        lock (_gate)
            _members.Add(principal);
    }

    /// <summary>Appends a message from a person or an agent, as the wire would, and returns its seq.</summary>
    public long Post(AuthorKind kind, Guid author, string text, params Guid[] mentions)
    {
        lock (_gate)
        {
            var seq = ++_head;
            var messageId = Guid.NewGuid();
            _messages.Add(new ResponderMessage(seq, messageId, kind, author, text, mentions));
            _keys[(author, $"post:{seq}")] = new AppendResult(GcId, seq, messageId, Duplicate: false, DateTimeOffset.UnixEpoch);
            return seq;
        }
    }

    public Task<ResponderGcSnapshot?> GetSnapshotAsync(Guid gcId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (gcId != GcId)
                return Task.FromResult<ResponderGcSnapshot?>(null);
            return Task.FromResult<ResponderGcSnapshot?>(new ResponderGcSnapshot(Archived, new HashSet<Guid>(_members)));
        }
    }

    public Task<IReadOnlyList<ResponderMessage>> ReadNewMessagesAsync(Guid gcId, long afterSeq, long throughSeq, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<ResponderMessage> rows = _messages
                .Where(m => m.Seq > afterSeq && m.Seq <= throughSeq)
                .Take(limit)
                .ToArray();
            return Task.FromResult(rows);
        }
    }

    public Task<IReadOnlyList<ResponderMessage>> ReadHistoryAsync(Guid gcId, long throughSeq, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<ResponderMessage> rows = _messages
                .Where(m => m.Seq <= throughSeq)
                .TakeLast(limit)
                .ToArray();
            return Task.FromResult(rows);
        }
    }

    public Task<bool> HasEventAsync(Guid gcId, Guid authorId, string clientMsgId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_keys.ContainsKey((authorId, clientMsgId)));
    }

    public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            if (request.GcId != GcId)
                throw new GcNotFoundException(request.GcId);
            if (Archived)
                throw new GcArchivedException(request.GcId);

            var key = (request.AuthorId, request.ClientMsgId);
            if (_keys.TryGetValue(key, out var original))
                return Task.FromResult(original with { Duplicate = true });

            var seq = ++_head;
            var eventId = Guid.NewGuid();
            var text = request.Message is { } body ? System.Text.Encoding.UTF8.GetString(body.BodyCiphertext.Span) : "";
            _messages.Add(new ResponderMessage(seq, eventId, request.AuthorKind, request.AuthorId, text, request.Mentions));
            var result = new AppendResult(GcId, seq, eventId, Duplicate: false, DateTimeOffset.UnixEpoch);
            _keys[key] = result;
            return Task.FromResult(result);
        }
    }

    public async Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct)
    {
        var results = new List<AppendResult>();
        foreach (var request in requests)
            results.Add(await AppendAsync(request, ct));
        return results;
    }
}

/// <summary>Presents the scripted <see cref="FakeProvider"/> as the gateway the responder calls.</summary>
internal sealed class ScriptedGateway(FakeProvider provider) : IModelGateway
{
    public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken) =>
        provider.StreamAsync(request, cancellationToken);
}

/// <summary>A notification source the test feeds by hand. It ends when the host stops.</summary>
internal sealed class ManualNotificationSource : IGcNotificationSource
{
    private readonly Channel<GcNotification> _channel = Channel.CreateUnbounded<GcNotification>();

    public void Send(GcNotification notification) => _channel.Writer.TryWrite(notification);

    public async IAsyncEnumerable<GcNotification> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct))
        {
            while (_channel.Reader.TryRead(out var notification))
                yield return notification;
        }
    }
}

internal static class ResponderTestIds
{
    public static readonly Guid Gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");
    public static readonly Guid OtherGc = Guid.Parse("0196a6f0-0000-7000-8000-000000000302");
    public static readonly Guid Owner = Guid.Parse("0196a6f0-0000-7000-8000-000000000311");
    public static readonly Guid Human = Guid.Parse("0196a6f0-0000-7000-8000-000000000312");
    public static readonly Guid Charles = Guid.Parse("0196a6f0-0000-7000-8000-000000000321");
    public static readonly Guid Juniper = Guid.Parse("0196a6f0-0000-7000-8000-000000000322");
    public static readonly Guid Outsider = Guid.Parse("0196a6f0-0000-7000-8000-000000000323");

    public static AgentResponderPersona CharlesPersona { get; } = new()
    {
        AgentId = Charles,
        Name = "Charles",
        SystemPrompt = "You are Charles, a careful engineer.",
    };

    public static AgentResponderPersona JuniperPersona { get; } = new()
    {
        AgentId = Juniper,
        Name = "Juniper",
        SystemPrompt = "You are Juniper, a reviewer.",
    };
}
