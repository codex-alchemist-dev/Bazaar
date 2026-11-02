namespace Bazaar.Client.LocalInference.Escalation;

/// <summary>Operator-set options for the escalation adapter. The default mode is the only place <see cref="EscalationMode.Explicit"/> is set.</summary>
public sealed record EscalationOptions
{
    /// <summary>Default for <c>coworker.escalation.mode</c> (doc 31, Settings).</summary>
    public const EscalationMode DefaultMode = EscalationMode.Explicit;

    public EscalationMode Mode { get; init; } = DefaultMode;
}
