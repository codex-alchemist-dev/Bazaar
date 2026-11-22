using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.VmEgress.Approvals.Security;

/// <summary>
/// <see cref="IEgressApprovalPort"/> over the owner approvals service (doc 15). One approval row per agent and additions key, bound by
/// the service's dispatch hash to the exact additions. Tickets are indexed in memory; a restart loses the index, files a fresh card,
/// and the old card expires on its own.
/// </summary>
/// <remarks>
/// Egress growth has no turn session, so the approval's session id is derived from the agent id (stable, never a real session).
/// The tier is carried by the tool name alone; this port only enforces the floor of 2.
/// </remarks>
public sealed class ApprovalServiceEgressApprovalPort : IEgressApprovalPort, IDisposable
{
    /// <summary>The tool name every egress approval is filed under.</summary>
    public const string ToolName = "vm.egress.allowlist_change";

    private const int _tierFloor = 2;

    private readonly Func<ApprovalService> _service;
    private readonly Func<IApprovalStore> _store;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(string AgentId, string AdditionsKey), Guid> _index = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the port over a service and the store it writes to.</summary>
    public ApprovalServiceEgressApprovalPort(ApprovalService service, IApprovalStore store, TimeProvider time)
        : this(Fixed(service), Fixed(store), time)
    {
    }

    /// <summary>Creates the port with lazy dependencies, so a missing registration fails the call (and the gate denies) rather than the resolve.</summary>
    internal ApprovalServiceEgressApprovalPort(Func<ApprovalService> service, Func<IApprovalStore> store, TimeProvider time)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<EgressApprovalTicket> RequestOrGetAsync(EgressApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tier < _tierFloor)
        {
            throw new InvalidOperationException($"Egress growth needs approval tier {_tierFloor} or higher.");
        }

        var service = _service();
        var store = _store();
        var key = (request.AgentId, request.AdditionsKey);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_index.TryGetValue(key, out var id))
            {
                var existing = await store.GetAsync(id, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    switch (EffectiveStatus(existing, now))
                    {
                        case ApprovalStatus.Pending:
                            return new EgressApprovalTicket(id, request.AdditionsKey, EgressTicketStatus.Pending);
                        case ApprovalStatus.Approved:
                            return new EgressApprovalTicket(id, request.AdditionsKey, EgressTicketStatus.Approved);
                        case ApprovalStatus.Denied:
                            _index.TryRemove(key, out _);
                            return new EgressApprovalTicket(id, request.AdditionsKey, EgressTicketStatus.Denied);
                        case ApprovalStatus.Expired:
                            _index.TryRemove(key, out _);
                            return new EgressApprovalTicket(id, request.AdditionsKey, EgressTicketStatus.Expired);
                    }
                }

                // Consumed or gone: retired. File fresh below.
                _index.TryRemove(key, out _);
            }

            var context = new DispatchContext(
                request.AgentId,
                SessionIdFor(request.AgentId),
                SessionRoles.Reader,
                ToolName,
                null,
                request.AdditionsKey,
                ArgsFor(request),
                SessionTaint.Clean,
                now);

            var result = await service.RequestAsync(context, ct).ConfigureAwait(false);
            if (result.Outcome != ApprovalService.RequestOutcome.Created || result.Request is null)
            {
                throw new InvalidOperationException($"The approvals service did not file the egress request: {result.Outcome}.");
            }

            _index[key] = result.Request.Id;
            return new EgressApprovalTicket(result.Request.Id, request.AdditionsKey, EgressTicketStatus.Pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConsumeAsync(Guid ticketId, CancellationToken ct)
    {
        var row = await _store().GetAsync(ticketId, ct).ConfigureAwait(false);
        if (row is null || row.Status != ApprovalStatus.Approved || row.Tool != ToolName)
        {
            return false;
        }

        return await _service().ConsumeAsync(ticketId, _time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Stable id for an agent's egress approvals: first 16 bytes of SHA-256 over <c>egress-allowlist|agentId</c>.</summary>
    internal static Guid SessionIdFor(string agentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("egress-allowlist|" + agentId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static ApprovalStatus EffectiveStatus(ApprovalRequest row, DateTimeOffset now) =>
        row.Status is ApprovalStatus.Pending or ApprovalStatus.Approved && now >= row.ExpiresAt ? ApprovalStatus.Expired : row.Status;

    private static JsonElement ArgsFor(EgressApprovalRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("agentId", request.AgentId);
            writer.WriteStartArray("additions");
            foreach (var addition in request.Additions)
            {
                writer.WriteStringValue(addition);
            }

            writer.WriteEndArray();
            writer.WriteString("additionsKey", request.AdditionsKey);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static Func<T> Fixed<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return () => value;
    }
}
