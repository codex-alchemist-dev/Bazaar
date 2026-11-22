using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.VmEgress.Approvals;

/// <summary>
/// <see cref="IEgressApprovalGate"/> over the owner approvals service (doc 16 egress, doc 15 approvals). Allowlist growth is a
/// Tier 2 action: a hard-tainted agent is refused outright, otherwise a request goes to the owner and the answer is Pending until
/// the owner decides. An approval is bound to the hash of the exact additions and is used up by the answer that returns Approved.
/// Fails closed: an error, a timeout, a mismatched ticket or an Approved row that cannot be audited is Denied. Every decision
/// (granted, pending, denied) writes one audit row. A cancelled caller gets the cancellation, and no grant is spent.
/// </summary>
public sealed class EgressApprovalGate(
    IEgressApprovalPort approvals,
    IEgressTaintReader taint,
    IAuditLog audit,
    EgressApprovalGateOptions options,
    TimeProvider time) : IEgressApprovalGate
{
    /// <summary>Audit kind for every gate decision.</summary>
    public const string AuditKind = "egress.approval";

    private const string _actor = "vm-egress-approvals";

    /// <inheritdoc />
    public async Task<EgressApprovalState> RequestGrowthAsync(string agentId, IReadOnlyList<string> additions, int tier, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(additions);
        ct.ThrowIfCancellationRequested();

        var canonical = additions.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var effectiveTier = Math.Max(tier, EgressApprovalGateOptions.TierFloor);
        if (canonical.Length == 0)
        {
            return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "no-additions", null, ct).ConfigureAwait(false);
        }

        bool tainted;
        try
        {
            tainted = await WithTimeout(token => taint.IsHardTaintedAsync(agentId, token).AsTask(), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsFailure(e, ct))
        {
            return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "taint-check-failed", null, ct).ConfigureAwait(false);
        }

        if (tainted)
        {
            return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "hard-tainted", null, ct).ConfigureAwait(false);
        }

        var key = Key(canonical);
        EgressApprovalTicket? ticket;
        try
        {
            ticket = await WithTimeout(token => approvals.RequestOrGetAsync(new EgressApprovalRequest(agentId, canonical, key, effectiveTier), token), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsFailure(e, ct))
        {
            return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "approvals-unavailable", null, ct).ConfigureAwait(false);
        }

        if (ticket is null || !string.Equals(ticket.AdditionsKey, key, StringComparison.Ordinal))
        {
            return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "ticket-mismatch", ticket?.Id, ct).ConfigureAwait(false);
        }

        switch (ticket.Status)
        {
            case EgressTicketStatus.Pending:
                return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Pending, "awaiting-owner", ticket.Id, ct).ConfigureAwait(false);
            case EgressTicketStatus.Approved:
                bool won;
                try
                {
                    won = await WithTimeout(token => approvals.ConsumeAsync(ticket.Id, token), ct).ConfigureAwait(false);
                }
                catch (Exception e) when (IsFailure(e, ct))
                {
                    return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "consume-failed", ticket.Id, ct).ConfigureAwait(false);
                }

                return won
                    ? await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Approved, "owner-approved", ticket.Id, ct).ConfigureAwait(false)
                    : await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "grant-already-used", ticket.Id, ct).ConfigureAwait(false);
            case EgressTicketStatus.Expired:
                return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "expired", ticket.Id, ct).ConfigureAwait(false);
            default:
                return await DecideAsync(agentId, canonical, effectiveTier, EgressApprovalState.Denied, "owner-denied", ticket.Id, ct).ConfigureAwait(false);
        }
    }

    // A failure means deny, except the caller's own cancellation, which must reach the caller.
    private static bool IsFailure(Exception e, CancellationToken caller) => !(e is OperationCanceledException && caller.IsCancellationRequested);

    private async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.PortTimeout);
        var task = call(cts.Token);

        // A port that ignores its token must still not hold the caller past the timeout.
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = await Task.WhenAny(task, Task.Delay(options.PortTimeout, time, delayCts.Token)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (done != task)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            throw new TimeoutException("The approvals service did not answer in time.");
        }

        await delayCts.CancelAsync().ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    // Writes the audit row, then returns the state. A row that cannot be written turns anything but a denial into a denial.
    private async Task<EgressApprovalState> DecideAsync(
        string agentId,
        string[] additions,
        int tier,
        EgressApprovalState state,
        string reason,
        Guid? ticketId,
        CancellationToken ct)
    {
        try
        {
            await audit.AppendAsync(new AuditEntry(time.GetUtcNow(), _actor, AuditKind, Payload(agentId, additions, tier, state, reason, ticketId)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (state != EgressApprovalState.Denied)
        {
            return EgressApprovalState.Denied;
        }
        catch (Exception)
        {
            // A denial stands even when its row cannot be written.
        }

        return state;
    }

    private static string Payload(string agentId, string[] additions, int tier, EgressApprovalState state, string reason, Guid? ticketId)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("agent", agentId);
            w.WriteString("decision", state.ToString());
            w.WriteString("reason", reason);
            w.WriteNumber("tier", tier);
            if (ticketId is { } id)
            {
                w.WriteString("approval_id", id);
            }

            w.WriteStartArray("additions");
            foreach (var a in additions)
            {
                w.WriteStringValue(a);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Length-prefixed, so no two different sets can hash from the same text.
    private static string Key(string[] canonical)
    {
        var sb = new StringBuilder();
        foreach (var a in canonical)
        {
            sb.Append(a.Length).Append(':').Append(a).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
