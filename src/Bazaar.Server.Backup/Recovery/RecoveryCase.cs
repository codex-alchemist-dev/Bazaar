using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Backup.Recovery;

/// <summary>Where a recovery attempt stands.</summary>
public enum RecoveryState
{
    /// <summary>A quorum member started the recovery; the lock has not begun.</summary>
    Initiated,

    /// <summary>The time lock is running; owner devices can cancel.</summary>
    Waiting,

    /// <summary>Lock elapsed and the threshold of distinct assertions is met.</summary>
    ReadyToComplete,

    /// <summary>Recovery completed. Terminal.</summary>
    Completed,

    /// <summary>An owner device cancelled. Terminal.</summary>
    Cancelled,
}

/// <summary>Outcome of one transition: the next case, the audit events to append, and any alerts to send. Nothing is persisted here.</summary>
public sealed record RecoveryTransition(
    RecoveryCase Case,
    bool Succeeded,
    IReadOnlyList<RecoveryAuditEvent> Events,
    IReadOnlyList<RecoveryAlert> Alerts);

/// <summary>
/// Immutable state of one recovery attempt (doc 48 section 7). Transitions are pure: time is always passed in, the
/// verifier is the only collaborator, and a refused transition returns the unchanged case plus a refusal event.
/// The owner's biometric check on the new device (section 8) is a separate gate applied by the caller.
/// </summary>
public sealed record RecoveryCase
{
    private RecoveryCase()
    {
    }

    /// <summary>Case identifier.</summary>
    public required string CaseId { get; init; }

    /// <summary>Current state.</summary>
    public required RecoveryState State { get; init; }

    /// <summary>The policy in force when the case was initiated.</summary>
    public required QuorumPolicy Policy { get; init; }

    /// <summary>The quorum member who initiated.</summary>
    public required string InitiatedBy { get; init; }

    /// <summary>When initiation happened.</summary>
    public required DateTimeOffset InitiatedAt { get; init; }

    /// <summary>When the lock ends; null until the lock starts.</summary>
    public DateTimeOffset? LockEndsAt { get; init; }

    /// <summary>Distinct members whose assertions were verified.</summary>
    public ImmutableSortedSet<string> AcceptedMembers { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    /// <summary>Starts a case and alerts every owner device and every backup owner.</summary>
    public static RecoveryTransition Initiate(
        string caseId,
        QuorumPolicy policy,
        string initiatingMemberId,
        IReadOnlyCollection<string> ownerDeviceIds,
        IReadOnlyCollection<string> backupOwnerIds,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatingMemberId);
        ArgumentNullException.ThrowIfNull(ownerDeviceIds);
        ArgumentNullException.ThrowIfNull(backupOwnerIds);

        var c = new RecoveryCase
        {
            CaseId = caseId,
            State = RecoveryState.Initiated,
            Policy = policy,
            InitiatedBy = initiatingMemberId,
            InitiatedAt = now,
        };

        var earliest = now + policy.RecoveryTimeLock;
        var alerts = new List<RecoveryAlert>();
        alerts.AddRange(ownerDeviceIds.Distinct(StringComparer.Ordinal)
            .Select(id => new RecoveryAlert(RecoveryAlertTarget.OwnerDevice, id, caseId, earliest)));
        alerts.AddRange(backupOwnerIds.Distinct(StringComparer.Ordinal)
            .Select(id => new RecoveryAlert(RecoveryAlertTarget.BackupOwner, id, caseId, earliest)));

        var ev = Event("recovery.initiated", c, now, ("initiatedBy", initiatingMemberId), ("alertCount", alerts.Count));
        return new RecoveryTransition(c, true, [ev], alerts);
    }

    /// <summary>Starts the time lock. Valid only from <see cref="RecoveryState.Initiated"/>.</summary>
    public RecoveryTransition StartTimeLock(DateTimeOffset now)
    {
        if (State != RecoveryState.Initiated)
        {
            return Refuse("recovery.lock.refused", now, "not-initiated");
        }

        var next = this with { State = RecoveryState.Waiting, LockEndsAt = now + Policy.RecoveryTimeLock };
        return new RecoveryTransition(next, true, [Event("recovery.lock.started", next, now, ("lockEndsAt", next.LockEndsAt!.Value))], []);
    }

    /// <summary>
    /// Verifies one hardware-key assertion. A valid one adds the member once; a repeat is accepted but does not count
    /// twice. Allowed only while the case is Waiting or ReadyToComplete.
    /// </summary>
    public RecoveryTransition AcceptAssertion(string memberId, ReadOnlyMemory<byte> assertion, IHardwareKeyAssertionVerifier verifier, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        ArgumentNullException.ThrowIfNull(verifier);

        if (State is not (RecoveryState.Waiting or RecoveryState.ReadyToComplete))
        {
            return Refuse("recovery.assertion.refused", now, "wrong-state", ("memberId", memberId));
        }

        if (!verifier.Verify(CaseId, memberId, assertion))
        {
            return Refuse("recovery.assertion.rejected", now, "invalid-assertion", ("memberId", memberId));
        }

        var duplicate = AcceptedMembers.Contains(memberId);
        var next = this with { AcceptedMembers = AcceptedMembers.Add(memberId) };
        var ev = Event("recovery.assertion.accepted", next, now, ("memberId", memberId), ("distinct", next.AcceptedMembers.Count), ("duplicate", duplicate));
        return new RecoveryTransition(next, true, [ev], []);
    }

    /// <summary>Moves Waiting to ReadyToComplete once the lock has elapsed and the threshold is met. Otherwise returns the case unchanged with no events.</summary>
    public RecoveryTransition Advance(DateTimeOffset now)
    {
        if (State == RecoveryState.Waiting && LockEndsAt is { } ends && now >= ends && AcceptedMembers.Count >= Policy.Threshold)
        {
            var next = this with { State = RecoveryState.ReadyToComplete };
            return new RecoveryTransition(next, true, [Event("recovery.ready", next, now)], []);
        }

        return new RecoveryTransition(this, false, [], []);
    }

    /// <summary>Completes the recovery. Refused unless the lock has elapsed and at least Threshold distinct assertions were accepted.</summary>
    public RecoveryTransition Complete(DateTimeOffset now)
    {
        if (State is not (RecoveryState.Waiting or RecoveryState.ReadyToComplete))
        {
            return Refuse("recovery.completion.refused", now, "wrong-state");
        }

        if (LockEndsAt is not { } ends || now < ends)
        {
            return Refuse("recovery.completion.refused", now, "time-lock-running");
        }

        if (AcceptedMembers.Count < Policy.Threshold)
        {
            return Refuse("recovery.completion.refused", now, "threshold-not-met", ("distinct", AcceptedMembers.Count));
        }

        var next = this with { State = RecoveryState.Completed };
        return new RecoveryTransition(next, true, [Event("recovery.completed", next, now, ("distinct", next.AcceptedMembers.Count))], []);
    }

    /// <summary>Cancels on behalf of an owner device (its biometric check is the caller's, section 8). Valid until the case is terminal.</summary>
    public RecoveryTransition Cancel(string ownerDeviceId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerDeviceId);

        if (State is RecoveryState.Completed or RecoveryState.Cancelled)
        {
            return Refuse("recovery.cancel.refused", now, "terminal", ("ownerDeviceId", ownerDeviceId));
        }

        var next = this with { State = RecoveryState.Cancelled };
        return new RecoveryTransition(next, true, [Event("recovery.cancelled", next, now, ("ownerDeviceId", ownerDeviceId))], []);
    }

    private RecoveryTransition Refuse(string kind, DateTimeOffset now, string reason, params (string Key, object Value)[] extra) =>
        new(this, false, [Event(kind, this, now, [("reason", reason), .. extra])], []);

    private static RecoveryAuditEvent Event(string kind, RecoveryCase c, DateTimeOffset at, params (string Key, object Value)[] extra)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("caseId", c.CaseId);
            w.WriteString("state", c.State.ToString());
            w.WriteString("at", at.ToString("O", CultureInfo.InvariantCulture));
            foreach (var (key, value) in extra)
            {
                switch (value)
                {
                    case int i:
                        w.WriteNumber(key, i);
                        break;
                    case bool b:
                        w.WriteBoolean(key, b);
                        break;
                    case DateTimeOffset d:
                        w.WriteString(key, d.ToString("O", CultureInfo.InvariantCulture));
                        break;
                    default:
                        w.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            w.WriteEndObject();
        }

        return new RecoveryAuditEvent(kind, Encoding.UTF8.GetString(ms.ToArray()));
    }
}
