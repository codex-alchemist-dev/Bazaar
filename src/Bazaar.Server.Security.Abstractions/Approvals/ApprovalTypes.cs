using System.Collections.Immutable;
using Bazaar.Approvals;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Security.Approvals;

/// <summary>Lifecycle of one approval request (doc 15 <c>approval.status</c>). Only the service moves it, with compare-and-set.</summary>
public enum ApprovalStatus
{
    /// <summary>Waiting for the owner. Nothing runs.</summary>
    Pending,

    /// <summary>The owner approved this exact call. It runs once.</summary>
    Approved,

    /// <summary>The owner refused. Terminal.</summary>
    Denied,

    /// <summary>No decision arrived before expiry. Terminal; expired approvals cannot be reused.</summary>
    Expired,

    /// <summary>The approved call ran. Terminal; the grant cannot be used again.</summary>
    Consumed,
}

/// <summary>The owner's choice on a card.</summary>
public enum ApprovalDecision
{
    /// <summary>Approve this exact call.</summary>
    Approve,

    /// <summary>Refuse this call.</summary>
    Deny,
}

/// <summary>One row of the <c>approval</c> table (doc 15). Immutable; the store replaces the whole row on each change.</summary>
/// <param name="Id">Approval id. Also the id in the signed message.</param>
/// <param name="AgentId">Agent that asked.</param>
/// <param name="SessionId">Session the call came from. <see cref="Guid.Empty"/> means "no session": the row was enqueued by a module (<c>IOwnerApprovalEnqueuer</c>), not by a tool dispatch. Nothing may treat it as a live session.</param>
/// <param name="Tool">Tool the approval covers.</param>
/// <param name="Target">Target of the action, if any. Part of the hash.</param>
/// <param name="ArgsHash">Dispatch hash at request time (<see cref="Policy.DispatchHash"/>). The only thing a decision is bound to.</param>
/// <param name="PayloadJson">The exact arguments the owner sees on the card.</param>
/// <param name="TaintSources">Taint lineage shown on the card, marked as quoted.</param>
/// <param name="Status">Current status.</param>
/// <param name="RequestedAt">When the request was made.</param>
/// <param name="ExpiresAt">Pending requests and grants are void at or after this time.</param>
/// <param name="DecidedBy">Owner id from the device key that signed the decision.</param>
/// <param name="DeviceId">Device key id that signed the decision.</param>
/// <param name="DecidedAt">When the decision was accepted.</param>
/// <param name="Signature">The owner's signature over the decision message.</param>
/// <param name="ConsumedAt">When an approved call ran.</param>
public sealed record ApprovalRequest(
    Guid Id,
    string AgentId,
    Guid SessionId,
    string Tool,
    string? Target,
    byte[] ArgsHash,
    string PayloadJson,
    ImmutableArray<TaintSource> TaintSources,
    ApprovalStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? DecidedBy = null,
    string? DeviceId = null,
    DateTimeOffset? DecidedAt = null,
    byte[]? Signature = null,
    DateTimeOffset? ConsumedAt = null);

/// <summary>Persistence for approval rows. Every change is a compare-and-set on <see cref="ApprovalRequest.Status"/>.</summary>
public interface IApprovalStore
{
    /// <summary>Adds a new row. Throws if the id already exists.</summary>
    Task InsertAsync(ApprovalRequest request, CancellationToken ct = default);

    /// <summary>The row, or null when no such approval exists.</summary>
    Task<ApprovalRequest?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Replaces the row with <paramref name="updated"/> only if its stored status is still <paramref name="expected"/>.
    /// Returns false when another change got there first.
    /// </summary>
    Task<bool> TryReplaceAsync(Guid id, ApprovalStatus expected, ApprovalRequest updated, CancellationToken ct = default);
}

/// <summary>Operator settings for approvals (doc 15 settings table). Values outside the stated range fail at startup.</summary>
/// <param name="ExpiryHours">security.approval.expiry_hours. Between 1 and 168; default 24.</param>
/// <param name="RequestCapPerAgentHour">security.approval.request_cap_per_agent_hour. Between 1 and 100; default 10.</param>
public sealed record ApprovalOptions(int ExpiryHours = 24, int RequestCapPerAgentHour = 10)
{
    /// <summary>Checks the ranges.</summary>
    public void Validate()
    {
        if (ExpiryHours is < 1 or > 168)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpiryHours), ExpiryHours, "Approval expiry must be 1 to 168 hours.");
        }

        if (RequestCapPerAgentHour is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestCapPerAgentHour), RequestCapPerAgentHour, "The approval request cap must be 1 to 100 per hour.");
        }
    }
}
