// TODO(component): move to the Company component project when PR #125 merges.
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Org;
using Bazaar.Server.Org.Data;
using Npgsql;

namespace Bazaar.Server.Company.Org;

/// <summary>What a loan change did.</summary>
public enum OrgLoanOutcome
{
    Applied,
    Refused,
    Invalid,
    NotFound,
    Conflict,
}

/// <summary>The result of a loan change. <see cref="Relation"/> is the stored row after the change, or the row seen when it did not apply.</summary>
public sealed record OrgLoanResult(OrgLoanOutcome Outcome, OrgRelation? Relation = null, string? Reason = null);

/// <summary>A request to lend an agent to a project. <paramref name="PlannedEndsAt"/> is the end date the GC line announces.</summary>
public sealed record StartLoanRequest(
    Guid LentAgentId,
    Guid LeadAgentId,
    Guid ProjectId,
    DateTimeOffset StartsAt,
    DateTimeOffset PlannedEndsAt);

/// <summary>
/// Starts and ends loans (doc 24 section 2). A loan adds the agent to the project's member list; it does not touch the home
/// schedule, availability or Soul, and it grants no access. Ending a loan does not touch Kanban: open cards stay with their
/// owner, and the GC line tells the project lead to reassign or extend.
/// <para>
/// GC seam: <see cref="IGcLogWriter"/> opens its own transaction, so the line is appended after the relation change commits.
/// The client message id is <c>org:{relationId}:{change}</c>, so <see cref="AnnounceAsync"/> can be run again after a failed
/// append without writing a second line. If the append throws, the change stays committed and the exception reaches the caller.
/// </para>
/// <para>
/// The relation table has no end date at insert, so a loan stays live until <see cref="EndAsync"/> is called; the planned end
/// is announced in the GC line and the caller's scheduler ends the loan then.
/// </para>
/// </summary>
public sealed class OrgLoanService(OrgRelationRepository relations, IGcLogWriter gcWriter, TimeProvider? time = null)
{
    private const string _loanStarted = "loan_started";
    private const string _loanEnded = "loan_ended";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Starts a loan if the actor may approve it, then announces it in <paramref name="noticeGcId"/>.</summary>
    public async Task<OrgLoanResult> StartAsync(
        StartLoanRequest request, OrgActor actor, OrgSettings settings, Guid noticeGcId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(settings);
        _ = SourceTrustFor(actor.Kind);

        if (request.PlannedEndsAt <= request.StartsAt)
            return new OrgLoanResult(OrgLoanOutcome.Invalid, Reason: "A loan must end after it starts.");

        var scope = await relations.ListByScopeAsync(request.ProjectId, ct: ct).ConfigureAwait(false);
        var verdict = LoanRules.MayStart(
            actor, request.LentAgentId, request.LeadAgentId, request.ProjectId, scope, _time.GetUtcNow(), settings);
        if (!verdict.Allowed)
            return new OrgLoanResult(OrgLoanOutcome.Refused, Reason: verdict.Reason);

        OrgRelation loan;
        try
        {
            loan = await relations.InsertAsync(
                new NewOrgRelation(
                    Guid.CreateVersion7(),
                    OrgRelationKind.LentTo,
                    request.LentAgentId,
                    request.LeadAgentId,
                    request.ProjectId,
                    request.StartsAt,
                    actor.PrincipalId),
                null,
                ct).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new OrgLoanResult(OrgLoanOutcome.Conflict, Reason: "This agent is already on loan to this project.");
        }

        await AnnounceAsync(loan, _loanStarted, request.PlannedEndsAt, actor, noticeGcId, ct).ConfigureAwait(false);
        return new OrgLoanResult(OrgLoanOutcome.Applied, loan);
    }

    /// <summary>
    /// Ends a loan at the current time if <paramref name="expectedVersion"/> is current, then posts the GC line that says it
    /// ended. Kanban is not touched.
    /// </summary>
    public async Task<OrgLoanResult> EndAsync(
        Guid relationId, long expectedVersion, OrgActor actor, OrgSettings settings, Guid noticeGcId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(settings);
        _ = SourceTrustFor(actor.Kind);

        var seen = await relations.GetAsync(relationId, ct).ConfigureAwait(false);
        if (seen is null)
            return new OrgLoanResult(OrgLoanOutcome.NotFound, Reason: "No relation has that id.");
        if (seen.Kind != OrgRelationKind.LentTo || seen.ProjectId is not { } project)
            return new OrgLoanResult(OrgLoanOutcome.Invalid, seen, "The relation is not a project loan.");
        if (seen.EndsAt is not null)
            return new OrgLoanResult(OrgLoanOutcome.Invalid, seen, "The loan has already ended.");

        var now = _time.GetUtcNow();
        var scope = await relations.ListByScopeAsync(project, ct: ct).ConfigureAwait(false);
        var verdict = LoanRules.MayEnd(actor, seen, scope, now, settings);
        if (!verdict.Allowed)
            return new OrgLoanResult(OrgLoanOutcome.Refused, seen, verdict.Reason);
        if (seen.Version != expectedVersion)
            return new OrgLoanResult(OrgLoanOutcome.Conflict, seen, $"Stored version is {seen.Version}.");

        // The end cannot precede the start (CHECK org_relation_span), so a loan that has not begun ends at its start.
        var endsAt = now < seen.StartsAt ? seen.StartsAt : now;
        var ended = await relations.EndAsync(relationId, expectedVersion, endsAt, null, ct).ConfigureAwait(false);
        if (ended is null)
            return new OrgLoanResult(OrgLoanOutcome.Conflict, seen, "Stored version changed during the update.");

        await AnnounceAsync(ended, _loanEnded, plannedEndsAt: null, actor, noticeGcId, ct).ConfigureAwait(false);
        return new OrgLoanResult(OrgLoanOutcome.Applied, ended);
    }

    /// <summary>
    /// Appends the GC line for a loan change. Idempotent per (relation, change), so it can be run again after a failed append.
    /// The line names ids, times and the change; for an ended loan it says open cards stay with their owner.
    /// </summary>
    public async Task AnnounceAsync(
        OrgRelation loan, string change, DateTimeOffset? plannedEndsAt, OrgActor actor, Guid noticeGcId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loan);
        ArgumentNullException.ThrowIfNull(actor);
        if (change is not (_loanStarted or _loanEnded))
            throw new ArgumentException("Unknown loan change.", nameof(change));

        var payload = new JsonObject
        {
            ["change"] = change,
            ["relation_id"] = loan.Id.ToString(),
            ["kind"] = "lent_to",
            ["lent_agent_id"] = loan.FromAgentId.ToString(),
            ["lead_agent_id"] = loan.ToAgentId.ToString(),
            ["project_id"] = loan.ProjectId?.ToString(),
            ["starts_at"] = JsonValue.Create(loan.StartsAt),
            ["ends_at"] = JsonValue.Create(loan.EndsAt),
            ["planned_ends_at"] = JsonValue.Create(plannedEndsAt),
            ["version"] = loan.Version,
        };
        if (change == _loanEnded)
            payload["open_cards"] = "stay_with_owner";

        var line = new AppendRequest(
            noticeGcId,
            GcEventKinds.System,
            actor.Kind,
            actor.PrincipalId,
            SourceTrustFor(actor.Kind),
            $"org:{loan.Id:D}:{change}")
        {
            PayloadJson = payload.ToJsonString(),
        };
        await gcWriter.AppendAsync(line, ct).ConfigureAwait(false);
    }

    private static SourceTrust SourceTrustFor(AuthorKind kind) => kind switch
    {
        AuthorKind.Owner => SourceTrust.Owner,
        AuthorKind.Agent => SourceTrust.Agent,
        _ => throw new ArgumentException("Loan changes come from the owner or an agent.", nameof(kind)),
    };
}
