using Bazaar.Contracts.Approvals;

namespace Bazaar.Server.Security.Approvals;

/// <summary>Why a client's decision was refused before it reached the approval service.</summary>
public enum DecisionRejection
{
    /// <summary>The decision is well formed and may go to the service.</summary>
    None,

    /// <summary>The decision value is not one the contract defines.</summary>
    UnknownDecision,

    /// <summary>The decision is defined but the service cannot record it yet.</summary>
    UnsupportedDecision,

    /// <summary>The device id has a disallowed shape.</summary>
    InvalidDeviceId,

    /// <summary>No signature was sent. A decision without a signature is never an approval.</summary>
    MissingSignature,

    /// <summary>The signature is not valid base64 or is not the 64-byte P1363 form.</summary>
    MalformedSignature,
}

/// <summary>A decision that passed shape checks, ready for <see cref="ApprovalService.DecideAsync"/>.</summary>
/// <param name="ApprovalId">Approval being decided.</param>
/// <param name="Decision">The service-level decision.</param>
/// <param name="DeviceId">Device key id that signed it.</param>
/// <param name="Signature">Decoded 64-byte signature.</param>
public sealed record ApprovalDecisionCommand(Guid ApprovalId, ApprovalDecision Decision, string DeviceId, byte[] Signature);

/// <summary>Result of <see cref="ApprovalDecisionMapper.Map"/>. <see cref="Command"/> is set only when accepted.</summary>
/// <param name="Rejection">Why the decision was refused, or <see cref="DecisionRejection.None"/>.</param>
/// <param name="Command">The command for the service, or null when refused.</param>
public sealed record DecisionMapResult(DecisionRejection Rejection, ApprovalDecisionCommand? Command)
{
    /// <summary>True when the decision may go to the service.</summary>
    public bool IsAccepted => Rejection == DecisionRejection.None;
}

/// <summary>
/// Checks a client's decision and maps it to the service's types. It never throws on bad input: unknown values,
/// missing or malformed signatures and bad device ids come back as a <see cref="DecisionRejection"/>.
/// </summary>
/// <remarks>
/// This only checks shape. The signature is verified against the stored hash and expiry by
/// <see cref="ApprovalService.DecideAsync"/>. The mapper never approves anything: an accepted command still needs the
/// service to verify it, and typed text from an agent or a dictated "yes" has no path into this mapper.
/// </remarks>
public static class ApprovalDecisionMapper
{
    /// <summary>Maps one decision. Accepts only a typed DTO with a 64-byte base64 signature.</summary>
    public static DecisionMapResult Map(ApprovalDecisionDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!Enum.IsDefined(dto.Decision))
        {
            return Reject(DecisionRejection.UnknownDecision);
        }

        var decision = dto.Decision switch
        {
            ApprovalDecisionKind.ApproveOnce => ApprovalDecision.Approve,
            ApprovalDecisionKind.Deny => ApprovalDecision.Deny,
            _ => (ApprovalDecision?)null,
        };

        if (decision is null)
        {
            return Reject(DecisionRejection.UnsupportedDecision);
        }

        if (!ApprovalSignature.IsValidDeviceId(dto.DeviceId))
        {
            return Reject(DecisionRejection.InvalidDeviceId);
        }

        if (dto.SignatureBase64 is null || dto.SignatureBase64.Length == 0)
        {
            return Reject(DecisionRejection.MissingSignature);
        }

        Span<byte> buffer = stackalloc byte[ApprovalSignature.SignatureLength];
        if (!Convert.TryFromBase64String(dto.SignatureBase64, buffer, out var written) || written != ApprovalSignature.SignatureLength)
        {
            return Reject(DecisionRejection.MalformedSignature);
        }

        var command = new ApprovalDecisionCommand(dto.ApprovalId, decision.Value, dto.DeviceId, buffer.ToArray());
        return new DecisionMapResult(DecisionRejection.None, command);
    }

    private static DecisionMapResult Reject(DecisionRejection reason) => new(reason, null);
}
