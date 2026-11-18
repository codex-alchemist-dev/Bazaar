namespace Bazaar.Server.Mail.Quarantine;

// Quarantine contracts (docs/design/22-email-subsystem.md, "Quarantine store slice"). Metadata only: no body, no full
// sender address. The raw content stays in the encrypted mail_message body and never crosses these types.

/// <summary>Where a quarantined message is. Names travel as strings on the pane model (doc 22, client mail screens).</summary>
public enum QuarantineState
{
    /// <summary>Held outside all agent context.</summary>
    Held,

    /// <summary>Released by the owner. Still untrusted: the taint stays.</summary>
    Released,

    /// <summary>Raw content purged after the retention period. The metadata entry stays.</summary>
    Purged,

    /// <summary>Reported to the provider's abuse channel, with per-report owner approval.</summary>
    Reported,
}

/// <summary>Who is asking for a quarantine change.</summary>
public enum QuarantineActorKind
{
    /// <summary>The owner, acting through the approval and owner-signature path.</summary>
    Owner,

    /// <summary>A reader session (tool-less, always hard-tainted). It can only add quarantine.</summary>
    Reader,

    /// <summary>An actor agent session. It may see a count and nothing else.</summary>
    Agent,

    /// <summary>The server's own pipeline and retention job.</summary>
    System,
}

/// <summary>One quarantine request's actor. <paramref name="HumanId"/> is set for the owner only.</summary>
public sealed record QuarantineActor(QuarantineActorKind Kind, Guid? HumanId = null)
{
    public static QuarantineActor Reader { get; } = new(QuarantineActorKind.Reader);

    public static QuarantineActor Agent { get; } = new(QuarantineActorKind.Agent);

    public static QuarantineActor System { get; } = new(QuarantineActorKind.System);

    public static QuarantineActor ForOwner(Guid humanId) => new(QuarantineActorKind.Owner, humanId);
}

/// <summary>
/// What a quarantine list or digest may know about one held message. Only the sender's domain is kept, never the full address.
/// <see cref="Tainted"/> is always true: release does not clean a message (doc 22, Questions 4).
/// </summary>
/// <param name="Id">The mail message id.</param>
/// <param name="PersonId">The person whose key encrypts the raw content; legal holds are checked against it.</param>
/// <param name="SenderDomain">The sender's domain, lowercased.</param>
/// <param name="ReasonCodes">The screening reason names, for example <c>AuthenticationFailed</c>.</param>
/// <param name="AuthResult">A short auth summary such as <c>spf=pass dkim=fail</c>.</param>
/// <param name="ReceivedAt">When the message arrived.</param>
/// <param name="State">The current state.</param>
/// <param name="RetentionUntil">An explicit purge time from the store, or null to derive it from the retention setting.</param>
public sealed record QuarantineItem(
    Guid Id,
    Guid PersonId,
    string SenderDomain,
    IReadOnlyList<string> ReasonCodes,
    string AuthResult,
    DateTimeOffset ReceivedAt,
    QuarantineState State,
    DateTimeOffset? RetentionUntil = null)
{
    /// <summary>Always true. A released message is still untrusted input.</summary>
    public bool Tainted { get; } = true;
}

/// <summary>Why a transition was allowed or refused.</summary>
public enum QuarantineTransitionOutcome
{
    Allowed,
    NotOwner,
    ReaderCannotRelease,
    AgentCannotChange,
    NotDueForPurge,
    LegalHold,
    ReportNeedsApproval,
    IllegalTransition,
    NotFound,
}

/// <summary>Result of a transition request.</summary>
/// <param name="Outcome">Allowed or the reason it was refused.</param>
/// <param name="Item">The item after the change when allowed; the unchanged item when refused; null when not found.</param>
public sealed record QuarantineTransitionResult(QuarantineTransitionOutcome Outcome, QuarantineItem? Item)
{
    public bool IsAllowed => Outcome == QuarantineTransitionOutcome.Allowed;
}

/// <summary>Extra facts a transition may need. The state machine reads them; it does not look anything up.</summary>
/// <param name="RetentionDue">The retention period has passed for this item.</param>
/// <param name="LegalHold">An active legal hold covers the item's person.</param>
/// <param name="ReportApprovalId">The Bazaar.Approvals record of the owner's per-report approval, for held to reported.</param>
public sealed record QuarantineTransitionContext(bool RetentionDue = false, bool LegalHold = false, Guid? ReportApprovalId = null);

/// <summary>
/// The store behind quarantine. Implementations apply the state machine; a refused transition changes nothing.
/// A Postgres implementation needs columns the 0021 mail schema lacks (see doc 22, quarantine store slice, doc gaps).
/// </summary>
public interface IMailQuarantineStore
{
    /// <summary>Adds an item in the held state. Only a reader session or the system pipeline may add; an agent may not.</summary>
    Task<QuarantineTransitionResult> AdmitAsync(QuarantineItem item, QuarantineActor actor, CancellationToken ct = default);

    Task<QuarantineItem?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Items in a state (all states when null), oldest first. For the owner's pane and the retention job, never for agents.</summary>
    Task<IReadOnlyList<QuarantineItem>> ListAsync(QuarantineState? state, CancellationToken ct = default);

    /// <summary>Applies a transition under the state machine's rules.</summary>
    Task<QuarantineTransitionResult> TransitionAsync(
        Guid id, QuarantineState target, QuarantineActor actor, QuarantineTransitionContext context, CancellationToken ct = default);

    /// <summary>The number of held items. The only figure an agent sees.</summary>
    Task<int> CountHeldAsync(CancellationToken ct = default);
}

/// <summary>One row of the digest's top sender domains.</summary>
public sealed record TopSenderDomain(string Domain, int Count);

/// <summary>
/// The daily owner digest. Field names match the pane model keys <c>digest {held, released, purged, reported, topSenderDomains[{domain,count}]}</c>
/// plus <c>date</c> (doc 22, client mail screens). Counts and sender domains only.
/// </summary>
public sealed record QuarantineDigest(
    DateOnly Date, int Held, int Released, int Purged, int Reported, IReadOnlyList<TopSenderDomain> TopSenderDomains);

/// <summary>What an agent may know about quarantine: how many messages are held. Nothing else.</summary>
public sealed record QuarantineCountView(int Held);
