using Bazaar.Server.Mail.Quarantine;

namespace Bazaar.Server.MailInbound;

/// <summary>
/// An in-memory <see cref="IMailMessageStore"/> for tests and hosts without Postgres. Thread-safe; applies the same dedupe and access rules.
/// The Postgres store sees quarantine admission as the same row's update; here the optional <paramref name="quarantine"/> store answers whether a row is held.
/// </summary>
public sealed class InMemoryMailMessageStore(IMailQuarantineStore? quarantine = null) : IMailMessageStore
{
    private sealed class Row(NewInboundMessage message)
    {
        public NewInboundMessage Message { get; } = message;

        public bool Delivered { get; set; }

        public Guid? GcMessageId { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly List<Row> _rows = [];

    public async Task<InboundInsertResult> InsertReceivedAsync(NewInboundMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        Row? existing;
        lock (_gate)
        {
            existing = _rows.FirstOrDefault(r =>
                r.Message.MailboxId == message.MailboxId
                && (r.Message.BackendId == message.BackendId
                    || (message.RfcMessageId is not null && r.Message.RfcMessageId == message.RfcMessageId)));
            if (existing is null)
            {
                _rows.Add(new Row(message));
                return new InboundInsertResult(InboundInsertOutcome.Inserted, message.Id, InboundStoreState.Received, null);
            }
        }

        var held = quarantine is not null && await quarantine.GetAsync(existing.Message.Id, ct).ConfigureAwait(false) is not null;
        var state = existing.Delivered ? InboundStoreState.Delivered : held ? InboundStoreState.Held : InboundStoreState.Received;
        return new InboundInsertResult(InboundInsertOutcome.Duplicate, existing.Message.Id, state, existing.GcMessageId);
    }

    public async Task<IReadOnlyList<PendingInboundMessage>> ListPendingAsync(Guid mailboxId, int limit, CancellationToken ct)
    {
        var pending = await PendingRowsAsync(mailboxId, ct).ConfigureAwait(false);
        return [.. pending.Take(limit).Select(r => new PendingInboundMessage(
            r.Message.Id, r.Message.SenderDomain, r.Message.AuthSummary, r.Message.ScreenCodes, r.Message.ReceivedAt, r.Message.Body))];
    }

    public async Task<int> CountPendingAsync(Guid mailboxId, CancellationToken ct) => (await PendingRowsAsync(mailboxId, ct).ConfigureAwait(false)).Count;

    private async Task<List<Row>> PendingRowsAsync(Guid mailboxId, CancellationToken ct)
    {
        // OrderBy is stable, so equal receive times keep the order they were stored in.
        List<Row> candidates;
        lock (_gate)
        {
            candidates = [.. _rows.Where(r => r.Message.MailboxId == mailboxId && !r.Delivered)
                .OrderBy(r => r.Message.ReceivedAt)];
        }

        // A row quarantine has admitted is finished as far as this pipeline goes.
        var pending = new List<Row>(candidates.Count);
        foreach (var row in candidates)
        {
            if (quarantine is null || await quarantine.GetAsync(row.Message.Id, ct).ConfigureAwait(false) is null)
            {
                pending.Add(row);
            }
        }

        return pending;
    }

    public Task RecordGcMessageAsync(Guid id, Guid gcMessageId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Find(id) is { Delivered: false } row)
            {
                row.GcMessageId = gcMessageId;
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> MarkDeliveredAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Find(id) is not { Delivered: false } row)
            {
                return Task.FromResult(false);
            }

            row.Delivered = true;
            return Task.FromResult(true);
        }
    }

    public Task<MailBodyReadResult> ReadSealedBodyAsync(Guid id, MailAccessor accessor, CancellationToken ct)
    {
        if (!MailBodyAccessPolicy.CanReadRawBody(accessor))
        {
            return Task.FromResult(new MailBodyReadResult(MailBodyReadOutcome.Denied, null));
        }

        lock (_gate)
        {
            return Task.FromResult(Find(id) is { } row
                ? new MailBodyReadResult(MailBodyReadOutcome.Ok, row.Message.Body)
                : new MailBodyReadResult(MailBodyReadOutcome.NotFound, null));
        }
    }

    /// <summary>The stored messages and whether each was delivered, in insert order. For tests.</summary>
    public IReadOnlyList<(NewInboundMessage Message, bool Delivered, Guid? GcMessageId)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _rows.Select(r => (r.Message, r.Delivered, r.GcMessageId))];
        }
    }

    private Row? Find(Guid id) => _rows.FirstOrDefault(r => r.Message.Id == id);
}
