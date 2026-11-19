namespace Bazaar.Server.MailInbound;

/// <summary>
/// What a reader decides about a clean message. There is no release verdict: a reader can hand a message on or escalate it to
/// quarantine, and nothing else (doc 22 inbound step 5; the owner alone releases).
/// </summary>
public enum MailTriageVerdict
{
    /// <summary>Nothing needs the agent's attention beyond the announcement.</summary>
    Routine,

    /// <summary>The agent should look at the hand-over soon.</summary>
    NeedsAttention,

    /// <summary>Hold the message in quarantine. The pipeline admits it as a reader.</summary>
    Escalate,
}

/// <summary>
/// The closed vocabulary of <c>mail_triage</c> reason codes [PROPOSED; the schema registers in doc 15]. A hand-over carries codes from
/// this set and no free text, so injected text in a message cannot ride a hand-over to an actor.
/// </summary>
public static class MailTriageReasonCodes
{
    public const string SignUpConfirmation = "sign_up_confirmation";
    public const string VerificationCode = "verification_code";
    public const string OwnerRequest = "owner_request";
    public const string Notification = "notification";
    public const string Newsletter = "newsletter";
    public const string PersonalReply = "personal_reply";
    public const string Bounce = "bounce";
    public const string Suspicious = "suspicious";
    public const string PromptInjectionSuspected = "prompt_injection_suspected";
    public const string RequestsCredentials = "requests_credentials";
    public const string RequestsPayment = "requests_payment";
    public const string Unclear = "unclear";

    /// <summary>Added by the pipeline when a hand-over is invalid; never produced by a reader.</summary>
    public const string InvalidHandover = "invalid_handover";

    /// <summary>The codes a reader may use. <see cref="InvalidHandover"/> is the pipeline's own.</summary>
    public static IReadOnlyList<string> ReaderCodes { get; } =
    [
        SignUpConfirmation, VerificationCode, OwnerRequest, Notification, Newsletter, PersonalReply, Bounce,
        Suspicious, PromptInjectionSuspected, RequestsCredentials, RequestsPayment, Unclear,
    ];

    /// <summary>The most codes one hand-over may carry.</summary>
    public const int MaxCodes = 5;

    public static bool IsReaderCode(string code) => ReaderCodes.Contains(code, StringComparer.Ordinal);
}

/// <summary>The <c>mail_triage</c> hand-over: a verdict and reason codes, nothing else.</summary>
/// <param name="MailMessageId">The message the reader read.</param>
/// <param name="Verdict">What the reader decided.</param>
/// <param name="ReasonCodes">Codes from <see cref="MailTriageReasonCodes.ReaderCodes"/>, at most <see cref="MailTriageReasonCodes.MaxCodes"/>.</param>
public sealed record MailTriageHandover(Guid MailMessageId, MailTriageVerdict Verdict, IReadOnlyList<string> ReasonCodes)
{
    /// <summary>The hand-over schema id (doc 15).</summary>
    public const string SchemaId = "mail_triage";

    /// <summary>True when the codes are all from the reader set, at most <see cref="MailTriageReasonCodes.MaxCodes"/>, with no repeats.</summary>
    public bool IsValid() =>
        Enum.IsDefined(Verdict)
        && ReasonCodes is not null
        && ReasonCodes.Count <= MailTriageReasonCodes.MaxCodes
        && ReasonCodes.All(MailTriageReasonCodes.IsReaderCode)
        && ReasonCodes.Distinct(StringComparer.Ordinal).Count() == ReasonCodes.Count;
}
