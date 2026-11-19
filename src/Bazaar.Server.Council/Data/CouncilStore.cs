using System.Text.Json;
using Bazaar.Server.Council.Service;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Council.Data;

/// <summary>
/// SQL-first Council store over <c>council_vote</c>, <c>council_ballot</c>, <c>council_ballot_ver</c> and
/// <c>council_transition</c> (migration council/0001, doc 10 WP3). Every state change is one transaction that also appends
/// <c>council_transition</c>. Vote transitions use compare-and-set on <c>version</c>. Ballot reasoning is sealed and
/// opened through <see cref="ICouncilReasoningCipher"/>; this class never sees key material.
/// </summary>
/// <remarks>
/// TODO(component): DI registration (for example <c>AddBazaarCouncilStore</c>) and the cipher's wiring belong to the host;
/// nothing here is registered in a host yet.
/// </remarks>
public sealed class CouncilStore(NpgsqlDataSource dataSource, ICouncilReasoningCipher cipher) : ICouncilStore
{
    private const string _voteColumns = """
        vote_id, gc_id, subject_ref, kind, motion_class, state, version, options::text, deadline_at, extensions,
        eligible_snapshot::text, proposer_id, subject_author_id, reveal_seed, vault_decision_no, created_at
        """;

    private const string _insertVoteSql = """
        INSERT INTO council_vote (vote_id, gc_id, subject_ref, kind, motion_class, state, version, options, deadline_at,
            extensions, eligible_snapshot, proposer_id, subject_author_id, reveal_seed, vault_decision_no, created_at)
        VALUES (@vote_id, @gc_id, @subject_ref, @kind, @motion_class, @state, @version, @options::jsonb, @deadline_at,
            @extensions, @eligible_snapshot::jsonb, @proposer_id, @subject_author_id, @reveal_seed, @vault_decision_no, @created_at)
        """;

    private const string _insertTransitionSql = """
        INSERT INTO council_transition (vote_id, version, from_state, to_state, reason, at)
        VALUES (@vote_id, @version, @from_state, @to_state, @reason, @at)
        """;

    private const string _selectStateForUpdateSql = """
        SELECT state FROM council_vote WHERE vote_id = @vote_id AND version = @expected_version FOR UPDATE
        """;

    // COALESCE keeps the stored deadline and extension count when the transition does not change them.
    private const string _transitionSql = """
        UPDATE council_vote
        SET state = @state, version = version + 1,
            deadline_at = COALESCE(@deadline_at, deadline_at),
            extensions = COALESCE(@extensions, extensions)
        WHERE vote_id = @vote_id AND version = @expected_version
        """;

    // Sets the number once; a repeat of the same number matches the row and changes nothing. Any other value matches no row.
    private const string _vaultDecisionNoSql = """
        UPDATE council_vote SET vault_decision_no = @decision_no
        WHERE vote_id = @vote_id AND (vault_decision_no IS NULL OR vault_decision_no = @decision_no)
        """;

    private const string _insertBallotSql = """
        INSERT INTO council_ballot (vote_id, voter_id, phase, choice, reasoning_key_id, reasoning_ct, reasoning_nonce,
            tainted_at_write, submitted_at)
        VALUES (@vote_id, @voter_id, @phase, @choice, @reasoning_key_id, @reasoning_ct, @reasoning_nonce,
            @tainted_at_write, @submitted_at)
        ON CONFLICT (vote_id, voter_id, phase) DO NOTHING
        """;

    private const string _insertBallotVersionSql = """
        INSERT INTO council_ballot_ver (vote_id, voter_id, phase, seq, choice, reasoning_key_id, reasoning_ct, reasoning_nonce, at)
        VALUES (@vote_id, @voter_id, @phase, 1, @choice, @reasoning_key_id, @reasoning_ct, @reasoning_nonce, @at)
        """;

    private const string _listBallotsSql = """
        SELECT vote_id, voter_id, phase, choice, reasoning_key_id, reasoning_ct, reasoning_nonce, tainted_at_write, submitted_at
        FROM council_ballot
        WHERE vote_id = @vote_id
        ORDER BY submitted_at, phase, voter_id
        """;

    /// <inheritdoc />
    public async Task<CouncilVoteRecord?> FindVoteAsync(Guid voteId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_voteColumns} FROM council_vote WHERE vote_id = @vote_id");
        cmd.Parameters.AddWithValue("vote_id", voteId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadVote(reader) : null;
    }

    /// <inheritdoc />
    public async Task InsertVoteAsync(CouncilVoteRecord vote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vote);
        if (vote.State != VoteState.Collecting || vote.Version != 1)
        {
            throw new ArgumentException("A new vote is inserted in state Collecting at version 1.", nameof(vote));
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(_insertVoteSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("vote_id", vote.VoteId);
            cmd.Parameters.AddWithValue("gc_id", vote.GcId);
            cmd.Parameters.AddWithValue("subject_ref", vote.SubjectRef);
            cmd.Parameters.AddWithValue("kind", CouncilText.ToSql(vote.Kind));
            cmd.Parameters.AddWithValue("motion_class", CouncilText.ToSql(vote.MotionClass));
            cmd.Parameters.AddWithValue("state", CouncilText.ToSql(vote.State));
            cmd.Parameters.AddWithValue("version", vote.Version);
            cmd.Parameters.Add(Jsonb("options", JsonSerializer.Serialize(vote.Options.ToList(), CouncilJsonContext.Default.ListString)));
            cmd.Parameters.AddWithValue("deadline_at", vote.DeadlineAt);
            cmd.Parameters.AddWithValue("extensions", vote.Extensions);
            cmd.Parameters.Add(Jsonb("eligible_snapshot", JsonSerializer.Serialize(
                vote.EligibleVoterIds.Order(StringComparer.Ordinal).ToList(), CouncilJsonContext.Default.ListString)));
            cmd.Parameters.AddWithValue("proposer_id", vote.ProposerId);
            cmd.Parameters.AddWithValue("subject_author_id", CouncilText.Nullable(vote.SubjectAuthorId));
            cmd.Parameters.AddWithValue("reveal_seed", vote.RevealSeed);
            cmd.Parameters.AddWithValue("vault_decision_no", CouncilText.Nullable(vote.VaultDecisionNo));
            cmd.Parameters.AddWithValue("created_at", vote.CreatedAt);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await AppendTransitionAsync(conn, tx, vote.VoteId, 1, null, VoteState.Collecting, "open", vote.CreatedAt, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryTransitionAsync(
        Guid voteId,
        long expectedVersion,
        VoteState target,
        string reason,
        DateTimeOffset at,
        DateTimeOffset? newDeadlineAt,
        int? newExtensions,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Lock the row at the expected version and read the state the transition leaves, for the history row.
        string? fromState;
        await using (var read = new NpgsqlCommand(_selectStateForUpdateSql, conn, tx))
        {
            read.Parameters.AddWithValue("vote_id", voteId);
            read.Parameters.AddWithValue("expected_version", expectedVersion);
            fromState = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (fromState is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        int updated;
        await using (var cmd = new NpgsqlCommand(_transitionSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("state", CouncilText.ToSql(target));
            cmd.Parameters.Add(new NpgsqlParameter("deadline_at", NpgsqlDbType.TimestampTz) { Value = (object?)newDeadlineAt ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("extensions", NpgsqlDbType.Integer) { Value = (object?)newExtensions ?? DBNull.Value });
            cmd.Parameters.AddWithValue("vote_id", voteId);
            cmd.Parameters.AddWithValue("expected_version", expectedVersion);
            updated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (updated != 1)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await AppendTransitionAsync(conn, tx, voteId, expectedVersion + 1, CouncilText.ParseState(fromState), target, reason, at, ct)
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task SetVaultDecisionNoAsync(Guid voteId, int decisionNo, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(decisionNo, 1);
        await using var cmd = dataSource.CreateCommand(_vaultDecisionNoSql);
        cmd.Parameters.AddWithValue("vote_id", voteId);
        cmd.Parameters.AddWithValue("decision_no", decisionNo);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
        {
            return;
        }

        // No row matched: the vote does not exist, or it already has a different decision number.
        throw new InvalidOperationException(
            $"Council vote {voteId} is missing or already has a different vault decision number than {decisionNo}.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredBallot>> ListBallotsAsync(Guid voteId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_listBallotsSql);
        cmd.Parameters.AddWithValue("vote_id", voteId);
        var result = new List<StoredBallot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var voterId = reader.GetString(1);
            var sealedText = new ReasoningCiphertext(reader.GetGuid(4), (byte[])reader.GetValue(5), (byte[])reader.GetValue(6));
            result.Add(new StoredBallot(
                reader.GetGuid(0),
                voterId,
                CouncilText.ParsePhase(reader.GetString(2)),
                reader.GetString(3),
                cipher.Decrypt(voterId, sealedText),
                reader.GetBoolean(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertBallotAsync(StoredBallot ballot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ballot);
        var sealedText = cipher.Encrypt(ballot.VoterId, ballot.Reasoning);
        var phase = CouncilText.ToSql(ballot.Phase);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Lock the vote row so a concurrent transition (reveal, close, withdraw) cannot interleave with this insert,
        // then check the state under the lock. Ballots are rejected outside Collecting and FollowUp.
        string? stateText;
        await using (var lockCmd = new NpgsqlCommand("SELECT state FROM council_vote WHERE vote_id = @vote_id FOR UPDATE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("vote_id", ballot.VoteId);
            stateText = await lockCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (stateText is null || !VoteStateMachine.CanAcceptBallot(CouncilText.ParseState(stateText), ballot.Phase))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        int inserted;
        await using (var cmd = new NpgsqlCommand(_insertBallotSql, conn, tx))
        {
            AddBallotParameters(cmd, ballot, phase, sealedText);
            cmd.Parameters.AddWithValue("tainted_at_write", ballot.TaintedAtWrite);
            cmd.Parameters.AddWithValue("submitted_at", ballot.SubmittedAt);
            inserted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (inserted != 1)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await using (var ver = new NpgsqlCommand(_insertBallotVersionSql, conn, tx))
        {
            AddBallotParameters(ver, ballot, phase, sealedText);
            ver.Parameters.AddWithValue("at", ballot.SubmittedAt);
            await ver.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static void AddBallotParameters(NpgsqlCommand cmd, StoredBallot ballot, string phase, ReasoningCiphertext sealedText)
    {
        cmd.Parameters.AddWithValue("vote_id", ballot.VoteId);
        cmd.Parameters.AddWithValue("voter_id", ballot.VoterId);
        cmd.Parameters.AddWithValue("phase", phase);
        cmd.Parameters.AddWithValue("choice", ballot.Choice);
        cmd.Parameters.AddWithValue("reasoning_key_id", sealedText.KeyId);
        cmd.Parameters.AddWithValue("reasoning_ct", sealedText.Ciphertext);
        cmd.Parameters.AddWithValue("reasoning_nonce", sealedText.Nonce);
    }

    private static async Task AppendTransitionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid voteId,
        long version,
        VoteState? from,
        VoteState to,
        string reason,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_insertTransitionSql, conn, tx);
        cmd.Parameters.AddWithValue("vote_id", voteId);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("from_state", CouncilText.Nullable(from is null ? null : CouncilText.ToSql(from.Value)));
        cmd.Parameters.AddWithValue("to_state", CouncilText.ToSql(to));
        cmd.Parameters.AddWithValue("reason", reason);
        cmd.Parameters.AddWithValue("at", at);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static NpgsqlParameter Jsonb(string name, string json) => new(name, NpgsqlDbType.Jsonb) { Value = json };

    private static CouncilVoteRecord ReadVote(NpgsqlDataReader r)
    {
        var options = JsonSerializer.Deserialize(r.GetString(7), CouncilJsonContext.Default.ListString) ?? [];
        var eligible = JsonSerializer.Deserialize(r.GetString(10), CouncilJsonContext.Default.ListString) ?? [];
        return new CouncilVoteRecord(
            VoteId: r.GetGuid(0),
            GcId: r.GetGuid(1),
            SubjectRef: r.GetString(2),
            Kind: CouncilText.ParseKind(r.GetString(3)),
            MotionClass: CouncilText.ParseMotionClass(r.GetString(4)),
            State: CouncilText.ParseState(r.GetString(5)),
            Version: r.GetInt64(6),
            Options: options,
            DeadlineAt: r.GetFieldValue<DateTimeOffset>(8),
            Extensions: r.GetInt32(9),
            EligibleVoterIds: new HashSet<string>(eligible, StringComparer.Ordinal),
            ProposerId: r.GetString(11),
            SubjectAuthorId: r.IsDBNull(12) ? null : r.GetString(12),
            RevealSeed: r.GetInt64(13),
            VaultDecisionNo: r.IsDBNull(14) ? null : r.GetInt32(14),
            CreatedAt: r.GetFieldValue<DateTimeOffset>(15));
    }
}

/// <summary>Maps Council enums to and from the snake_case text the migration's CHECK constraints allow.</summary>
internal static class CouncilText
{
    public static string ToSql(VoteKind kind) => kind switch
    {
        VoteKind.ApproveReject => "approve_reject",
        VoteKind.OptionChoice => "option_choice",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string ToSql(MotionClass motionClass) => motionClass switch
    {
        MotionClass.Ordinary => "ordinary",
        MotionClass.BiggerChange => "bigger_change",
        _ => throw new ArgumentOutOfRangeException(nameof(motionClass), motionClass, null),
    };

    public static string ToSql(VoteState state) => state switch
    {
        VoteState.Collecting => "collecting",
        VoteState.Revealed => "revealed",
        VoteState.FollowUp => "follow_up",
        VoteState.Decided => "decided",
        VoteState.Escalated => "escalated",
        VoteState.Withdrawn => "withdrawn",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static string ToSql(BallotPhase phase) => phase switch
    {
        BallotPhase.Independent => "independent",
        BallotPhase.FollowUp => "followup",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    public static VoteKind ParseKind(string text) => text switch
    {
        "approve_reject" => VoteKind.ApproveReject,
        "option_choice" => VoteKind.OptionChoice,
        _ => throw new FormatException($"Unknown council vote kind '{text}'."),
    };

    public static MotionClass ParseMotionClass(string text) => text switch
    {
        "ordinary" => MotionClass.Ordinary,
        "bigger_change" => MotionClass.BiggerChange,
        _ => throw new FormatException($"Unknown council motion class '{text}'."),
    };

    public static VoteState ParseState(string text) => text switch
    {
        "collecting" => VoteState.Collecting,
        "revealed" => VoteState.Revealed,
        "follow_up" => VoteState.FollowUp,
        "decided" => VoteState.Decided,
        "escalated" => VoteState.Escalated,
        "withdrawn" => VoteState.Withdrawn,
        _ => throw new FormatException($"Unknown council vote state '{text}'."),
    };

    public static BallotPhase ParsePhase(string text) => text switch
    {
        "independent" => BallotPhase.Independent,
        "followup" => BallotPhase.FollowUp,
        _ => throw new FormatException($"Unknown council ballot phase '{text}'."),
    };

    public static object Nullable<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    public static object Nullable(string? value) => (object?)value ?? DBNull.Value;
}
