using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// <see cref="IOwnerApprovalEnqueuer"/> over <see cref="IApprovalStore"/> (doc 15, enqueue entry point). It only inserts
/// pending rows. It never decides, signs or consumes, so it cannot approve anything.
/// </summary>
/// <remarks>
/// The approval id is derived from the tool name and the action key, so a repeat finds the same row. The store's unique id
/// is what makes one key one row: when two callers race, one insert wins and the other reads the winner. Fields with no
/// honest value in this flow: <c>SessionId</c> is <see cref="Guid.Empty"/> (no session; the <c>approval</c> table has no
/// foreign key on it), taint sources are empty (no session read anything), target is null. The args hash is the dispatch
/// hash of the tool and the payload, so the owner's signature binds to exactly what the card shows.
/// </remarks>
public sealed class OwnerApprovalEnqueuer : IOwnerApprovalEnqueuer
{
    private const string _idDomain = "bazaar.owner-approval-enqueue.v1";

    private readonly IApprovalStore _store;
    private readonly ApprovalOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the enqueuer. A null store throws, so a host without one fails closed.</summary>
    public OwnerApprovalEnqueuer(IApprovalStore store, ApprovalOptions? options = null, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new ApprovalOptions();
        _options.Validate();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The approval id a request with this tool and action key gets. Stable across processes.</summary>
    public static Guid DeriveId(string tool, string actionKey)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(actionKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', _idDomain, tool, actionKey)));
        Span<byte> bytes = hash.AsSpan(0, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8: custom, name-derived
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }

    /// <inheritdoc />
    public async Task<Guid> EnqueueAsync(OwnerApprovalEnqueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        ct.ThrowIfCancellationRequested();

        var id = DeriveId(request.Tool, request.ActionKey);
        var payloadJson = BuildPayload(request);
        using var doc = JsonDocument.Parse(payloadJson);
        var hash = DispatchHash.Compute(request.Tool, null, doc.RootElement, ImmutableArray<TaintSource>.Empty);

        var existing = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return Matching(existing, request, hash);
        }

        var now = _time.GetUtcNow();
        var row = new ApprovalRequest(
            id,
            request.AgentId,
            Guid.Empty,
            request.Tool,
            null,
            hash,
            payloadJson,
            ImmutableArray<TaintSource>.Empty,
            ApprovalStatus.Pending,
            now,
            now + TimeSpan.FromHours(_options.ExpiryHours));

        try
        {
            await _store.InsertAsync(row, ct).ConfigureAwait(false);
            return id;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A concurrent caller may have inserted the same key first (primary key conflict). Read the winner; if there is
            // none, the failure was something else and propagates.
            var winner = await _store.GetAsync(id, ct).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            return Matching(winner, request, hash);
        }
    }

    private static Guid Matching(ApprovalRequest existing, OwnerApprovalEnqueueRequest request, byte[] hash)
    {
        if (existing.AgentId != request.AgentId
            || existing.Tool != request.Tool
            || !existing.ArgsHash.AsSpan().SequenceEqual(hash))
        {
            throw new InvalidOperationException("The action key already belongs to a different approval request.");
        }

        return existing.Id;
    }

    private static string BuildPayload(OwnerApprovalEnqueueRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("actionKey", request.ActionKey);
            writer.WriteString("agentId", request.AgentId);
            writer.WriteString("summary", request.Summary);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Validate(OwnerApprovalEnqueueRequest request)
    {
        CheckText(request.AgentId, nameof(request.AgentId), OwnerApprovalEnqueueRequest.MaxIdentifierLength);
        CheckText(request.ActionKey, nameof(request.ActionKey), OwnerApprovalEnqueueRequest.MaxIdentifierLength);
        CheckText(request.Summary, nameof(request.Summary), OwnerApprovalEnqueueRequest.MaxSummaryLength);
        CheckText(request.Tool, nameof(request.Tool), OwnerApprovalEnqueueRequest.MaxIdentifierLength);
        if (!IsToolName(request.Tool))
        {
            throw new ArgumentException("Tool must be lowercase dotted words such as 'account.request'.", nameof(request));
        }
    }

    private static void CheckText(string? value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} must not be blank.", name);
        }

        if (value.Length > max)
        {
            throw new ArgumentException($"{name} is longer than {max} characters.", name);
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException($"{name} must not contain control characters.", name);
            }
        }

        if (value.Trim().Length != value.Length)
        {
            throw new ArgumentException($"{name} must not start or end with whitespace.", name);
        }
    }

    /// <summary>Two or more dotted segments, each a lowercase letter then lowercase letters, digits or underscores.</summary>
    private static bool IsToolName(string tool)
    {
        var segments = tool.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment[0] is < 'a' or > 'z')
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
