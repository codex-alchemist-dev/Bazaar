namespace Bazaar.Client.LocalInference.Escalation;

/// <summary>
/// Whether a local task may be sent to the server (doc 31, Settings: <c>coworker.escalation.mode</c>).
/// <c>Automatic</c> sends content off the device without a per-request confirmation; it needs counsel review
/// before it is offered to any operator, and only the operator sets it.
/// </summary>
public enum EscalationMode
{
    Off,
    Explicit,
    Automatic,
}

/// <summary>Where an escalated request is run. Only the cloud tier exists in v1.</summary>
public enum EscalationTier
{
    Cloud,
}

/// <summary>A local task that the device cannot finish alone, to be run on the server through the gateway.</summary>
public sealed record EscalationRequest(LocalTaskKind Kind, string Input, int MaxOutputTokens, string CorrelationId);

/// <summary>Reply from the server. A non-null <see cref="Error"/> is a transport failure; <see cref="Text"/> is then empty.</summary>
public sealed record EscalationReply(string Text, EscalationTier Tier, string? Error);

/// <summary>
/// Carries an escalation request to the server. The real wire binding (the escalation message in doc 02 and the
/// gateway request in doc 05) is a follow-up; this interface is the seam that binding will implement.
/// </summary>
public interface IEscalationTransport
{
    Task<EscalationReply> SendAsync(EscalationRequest request, CancellationToken ct);
}

/// <summary>Opaque token the UI passes back after the user confirms a pending escalation. Single use.</summary>
public sealed record EscalationConfirmationToken(string Value)
{
    /// <summary>Creates a new unguessable token.</summary>
    public static EscalationConfirmationToken New() => new(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
}

/// <summary>What the UI shows before an explicit escalation is sent. <see cref="ContentLeavesDevice"/> is always true.</summary>
public sealed record EscalationDecision(EscalationRequest Request, EscalationConfirmationToken Token, bool ContentLeavesDevice);

/// <summary>Result of asking the adapter to escalate. Every variant is a typed result; the adapter does not throw for these.</summary>
public abstract record EscalationOutcome;

/// <summary>Explicit mode: nothing was sent. The UI must show the decision and obtain the user's confirmation.</summary>
public sealed record EscalationPending(EscalationDecision Decision) : EscalationOutcome;

/// <summary>The transport returned a reply. Content left the device.</summary>
public sealed record EscalationSent(EscalationReply Reply, bool ContentLeavesDevice) : EscalationOutcome;

/// <summary>The request was not sent: escalation is off, or the confirmation token is unknown or already used.</summary>
public sealed record EscalationRefused(EscalationRefusalReason Reason) : EscalationOutcome;

/// <summary>The transport was called and failed. Content left the device before the failure.</summary>
public sealed record EscalationFailed(EscalationFailureKind Kind, string Message, bool ContentLeavesDevice) : EscalationOutcome;

public enum EscalationRefusalReason
{
    Disabled,
    UnknownConfirmation,
}

public enum EscalationFailureKind
{
    TransportReturnedError,
    TransportThrew,
}
