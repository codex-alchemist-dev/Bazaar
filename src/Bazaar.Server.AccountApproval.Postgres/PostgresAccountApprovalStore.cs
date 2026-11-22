using System.Data;
using Bazaar.Server.AccountApproval.Abstractions;
using Npgsql;

namespace Bazaar.Server.AccountApproval.Postgres;

/// <summary>
/// Durable <see cref="IAccountApprovalStore"/> over <c>account_approval_request</c>, <c>account_approval_vote</c> and
/// <c>account_approval_decision</c> (component migration account_approval 0001). Rows are only inserted. A vote and its decision go in
/// one transaction under a per-request advisory lock, and only while the latest decision is pending; the unique constraint on
/// (request, approver, kind) and the unique final-decision index refuse a double count and a second final decision even if the lock
/// were bypassed. A stored value this code does not know throws: an unreadable request is never read as approved.
/// </summary>
public sealed class PostgresAccountApprovalStore(NpgsqlDataSource dataSource) : IAccountApprovalStore
{
    private const string _lock = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))";
    private const string _insertRequest =
        "INSERT INTO account_approval_request (request_id, requester_agent_id, service, action, terms, terms_url, terms_version_or_hash, " +
        "service_on_signup_whitelist, involves_spend, in_another_persons_name, person_steps, created_at, site_vote_motion_kind, " +
        "site_vote_subject, site_vote_reference, site_vote_finished, site_vote_eligible, site_vote_approve, site_vote_reject, site_vote_abstain) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20) " +
        "ON CONFLICT (request_id) DO NOTHING";
    private const string _insertVote =
        "INSERT INTO account_approval_vote (request_id, approver_agent_id, kind, model_id, reasoning_hash, cast_at) " +
        "VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT ON CONSTRAINT account_approval_vote_once DO NOTHING";
    private const string _insertDecision =
        "INSERT INTO account_approval_decision (request_id, outcome, rule, pending_remaining, pending_quorum, person_steps, " +
        "agent_may_accept_terms, terms_to_accept, terms_version_or_hash, expires_at, approved_by) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
    private const string _latestOutcome =
        "SELECT outcome FROM account_approval_decision WHERE request_id = $1 ORDER BY decision_id DESC LIMIT 1";
    private const string _selectRequest =
        "SELECT request_id, requester_agent_id, service, action, terms, terms_url, terms_version_or_hash, service_on_signup_whitelist, " +
        "involves_spend, in_another_persons_name, person_steps, created_at, site_vote_motion_kind, site_vote_subject, site_vote_reference, " +
        "site_vote_finished, site_vote_eligible, site_vote_approve, site_vote_reject, site_vote_abstain " +
        "FROM account_approval_request WHERE request_id = $1";
    private const string _selectVotes =
        "SELECT approver_agent_id, kind, model_id, reasoning_hash, cast_at FROM account_approval_vote WHERE request_id = $1 ORDER BY vote_id";
    private const string _selectDecision =
        "SELECT outcome, rule, pending_remaining, pending_quorum, person_steps, agent_may_accept_terms, terms_to_accept, " +
        "terms_version_or_hash, expires_at, approved_by FROM account_approval_decision WHERE request_id = $1 ORDER BY decision_id DESC LIMIT 1";

    /// <inheritdoc />
    public async Task<AccountApprovalRecord?> GetAsync(string requestId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        // One snapshot for the three reads, so a vote cannot appear without the decision that came with it.
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        AccountApprovalRequest request;
        await using (var cmd = Command(connection, tx, _selectRequest, Text(requestId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            request = ReadRequest(reader);
        }

        var votes = new List<PeerVote>();
        await using (var cmd = Command(connection, tx, _selectVotes, Text(requestId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                votes.Add(new PeerVote(
                    request.RequestId,
                    request.Service,
                    reader.GetString(0),
                    Parse<PeerVoteKind>(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        AccountApprovalDecision decision;
        await using (var cmd = Command(connection, tx, _selectDecision, Text(requestId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Request '{requestId}' has no decision row.");
            }

            decision = ReadDecision(reader, request, votes);
        }

        return new AccountApprovalRecord(request, votes, decision);
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(AccountApprovalRequest request, AccountApprovalDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, tx, request.RequestId, ct).ConfigureAwait(false);

        var site = request.SiteVote;
        await using (var cmd = Command(
            connection,
            tx,
            _insertRequest,
            Text(request.RequestId),
            Text(request.RequesterAgentId),
            Text(request.Service),
            Text(request.Action.ToString()),
            Text(request.Terms.ToString()),
            Text(request.TermsUrl),
            Text(request.TermsVersionOrHash),
            Typed(request.ServiceOnSignupWhitelist),
            Typed(request.InvolvesSpend),
            Typed(request.InAnotherPersonsName),
            Typed((int)request.PersonSteps),
            Typed(Micros(request.CreatedAt)),
            Text(site?.MotionKind),
            Text(site?.Subject),
            Text(site?.VoteReference),
            Typed<bool?>(site?.Finished),
            Typed<int?>(site?.Eligible),
            Typed<int?>(site?.Approve),
            Typed<int?>(site?.Reject),
            Typed<int?>(site?.Abstain)))
        {
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        await InsertDecisionAsync(connection, tx, request.RequestId, decision, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<StoredVoteOutcome> TryAppendVoteAsync(PeerVote vote, AccountApprovalDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);
        ArgumentNullException.ThrowIfNull(decision);
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, tx, vote.RequestId, ct).ConfigureAwait(false);

        var latest = await LatestOutcomeAsync(connection, tx, vote.RequestId, ct).ConfigureAwait(false);
        if (latest is null)
        {
            return StoredVoteOutcome.UnknownRequest;
        }

        if (latest != _pending)
        {
            return StoredVoteOutcome.AlreadyDecided;
        }

        await using (var cmd = Command(
            connection,
            tx,
            _insertVote,
            Text(vote.RequestId),
            Text(vote.ApproverAgentId),
            Text(vote.Kind.ToString()),
            Text(vote.ModelId),
            Text(vote.ReasoningHash),
            Typed(Micros(vote.CastAt))))
        {
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                return StoredVoteOutcome.DuplicateVote;
            }
        }

        await InsertDecisionAsync(connection, tx, vote.RequestId, decision, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return StoredVoteOutcome.Stored;
    }

    /// <inheritdoc />
    public async Task<bool> TryDecideAsync(string requestId, AccountApprovalDecision decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentNullException.ThrowIfNull(decision);
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, tx, requestId, ct).ConfigureAwait(false);

        if (await LatestOutcomeAsync(connection, tx, requestId, ct).ConfigureAwait(false) != _pending)
        {
            return false;
        }

        await InsertDecisionAsync(connection, tx, requestId, decision, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private const string _pending = "pending";
    private const string _approved = "approved_by_peers";
    private const string _needsOwner = "needs_owner";
    private const string _denied = "denied";

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string requestId, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, _lock, Text(requestId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> LatestOutcomeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string requestId, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx, _latestOutcome, Text(requestId));
        return (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertDecisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string requestId, AccountApprovalDecision decision, CancellationToken ct)
    {
        var pending = decision as Pending;
        var approved = decision as ApprovedByPeers;
        var outcome = decision switch
        {
            Pending => _pending,
            ApprovedByPeers => _approved,
            NeedsOwner => _needsOwner,
            Denied => _denied,
            _ => throw new ArgumentException("Unknown decision type.", nameof(decision)),
        };

        await using var cmd = Command(
            connection,
            tx,
            _insertDecision,
            Text(requestId),
            Text(outcome),
            Text(decision.Rule.ToString()),
            Typed<int?>(pending?.Remaining),
            Typed<int?>(pending?.Quorum),
            Typed<int?>(approved is null ? null : (int)approved.PersonSteps),
            Typed<bool?>(approved?.AgentMayAcceptTerms),
            Text(approved?.TermsToAccept),
            Text(approved?.TermsVersionOrHash),
            Typed<DateTimeOffset?>(approved is null ? null : Micros(approved.ExpiresAt)),
            new NpgsqlParameter<string[]?> { TypedValue = approved is null ? null : [.. approved.Approvals.Select(v => v.ApproverAgentId)] });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static AccountApprovalRequest ReadRequest(NpgsqlDataReader r)
    {
        SiteVoteResult? site = r.IsDBNull(12)
            ? null
            : new SiteVoteResult(
                r.GetString(12), r.GetString(13), r.GetString(14), r.GetBoolean(15), r.GetInt32(16), r.GetInt32(17), r.GetInt32(18), r.GetInt32(19));
        return new AccountApprovalRequest(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            Parse<RequestedAction>(r.GetString(3)),
            Parse<ServiceTermsStatus>(r.GetString(4)),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.GetBoolean(7),
            r.GetBoolean(8),
            r.GetBoolean(9),
            (PersonStep)r.GetInt32(10),
            r.GetFieldValue<DateTimeOffset>(11),
            site);
    }

    private static AccountApprovalDecision ReadDecision(NpgsqlDataReader r, AccountApprovalRequest request, List<PeerVote> votes)
    {
        var id = request.RequestId;
        var rule = Parse<DecisionRule>(r.GetString(1));
        switch (r.GetString(0))
        {
            case _pending:
                return new Pending(id, r.GetInt32(2), r.GetInt32(3));
            case _denied:
                return new Denied(id, rule);
            case _needsOwner:
                return new NeedsOwner(id, rule, new ApprovalEvidence(request.Service, request.RequesterAgentId, request.TermsUrl, request.SiteVote, [.. votes]));
            case _approved:
                var approvers = r.GetFieldValue<string[]>(9);
                var approvals = approvers
                    .Select(a => votes.FirstOrDefault(v => v.Kind == PeerVoteKind.Approve && string.Equals(v.ApproverAgentId, a, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException($"Approval for request '{id}' names approver '{a}' who has no stored approve vote."))
                    .ToArray();
                return new ApprovedByPeers(
                    id,
                    rule,
                    approvals,
                    (PersonStep)r.GetInt32(4),
                    r.GetBoolean(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.GetFieldValue<DateTimeOffset>(8));
            default:
                throw new InvalidOperationException($"Request '{id}' has an unknown stored outcome.");
        }
    }

    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Unknown stored {typeof(T).Name} value.");

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, params NpgsqlParameter[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, connection, tx);
        foreach (var parameter in parameters)
        {
            cmd.Parameters.Add(parameter);
        }

        return cmd;
    }

    private static NpgsqlParameter<string?> Text(string? value) => new() { TypedValue = value };

    private static NpgsqlParameter<T> Typed<T>(T value) => new() { TypedValue = value };

    /// <summary>Cuts an instant to whole microseconds in UTC, the form Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset Micros(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
