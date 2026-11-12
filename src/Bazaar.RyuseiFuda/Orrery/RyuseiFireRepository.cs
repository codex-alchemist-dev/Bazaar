using System.Text.Json.Nodes;
using Npgsql;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// SQL-first access to <c>ryusei_fire</c> and the agent lookups the tick needs (doc 38, Data model). Every method takes the
/// tick's transaction, so a fire row and the trigger change it records commit together.
/// </summary>
/// <summary>
/// What a wake decision reads from the database, read in the decision's transaction: the targets' status, the fires and wakes
/// the guards count, and the last woke fire. The tick and the event bridge both build it through
/// <see cref="RyuseiFireRepository.LoadContextAsync"/>, so both apply the same guards to the same counts.
/// </summary>
public sealed record RyuseiFireContext(
    IReadOnlyList<RyuseiFireTarget> Targets,
    int FiresInLast24Hours,
    DateTimeOffset? LastWokeAt,
    int WakesInLastHour);

public static class RyuseiFireRepository
{
    // A fire already recorded for this trigger and instant is a no-op. The unique key is what makes a crash-retried fire safe.
    private const string _insertSql = """
        INSERT INTO ryusei_fire (fire_id, trigger_id, scheduled_for, outcome, cause, at)
        VALUES (@fire_id, @trigger_id, @scheduled_for, @outcome, @cause::jsonb, @at)
        ON CONFLICT (trigger_id, scheduled_for) DO NOTHING
        """;

    /// <summary>
    /// Appends one fire row. Returns false, and writes nothing, when this trigger already has a fire for
    /// <paramref name="scheduledFor"/>.
    /// </summary>
    public static async Task<bool> InsertAsync(
        NpgsqlTransaction tx,
        Guid fireId,
        Guid triggerId,
        DateTimeOffset scheduledFor,
        string outcome,
        JsonObject cause,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(cause);
        await using var cmd = new NpgsqlCommand(_insertSql, tx.Connection, tx);
        cmd.Parameters.AddWithValue("fire_id", fireId);
        cmd.Parameters.AddWithValue("trigger_id", triggerId);
        cmd.Parameters.AddWithValue("scheduled_for", scheduledFor);
        cmd.Parameters.AddWithValue("outcome", outcome);
        cmd.Parameters.AddWithValue("cause", cause.ToJsonString());
        cmd.Parameters.AddWithValue("at", at);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Reads the inputs one wake decision needs, in the caller's transaction. See <see cref="RyuseiFireContext"/>.</summary>
    public static async Task<RyuseiFireContext> LoadContextAsync(
        NpgsqlTransaction tx,
        RyuseiTriggerRecord trigger,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(trigger);
        var states = await AgentStatesAsync(tx, trigger.TargetAgentIds, ct).ConfigureAwait(false);
        var targets = trigger.TargetAgentIds
            .Select(id => new RyuseiFireTarget(id, RyuseiFireWriter.TargetStatus(states.GetValueOrDefault(id))))
            .ToArray();
        var firesInDay = await CountWokeSinceAsync(tx, trigger.TriggerId, now - TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        var lastWoke = await LastWokeAtAsync(tx, trigger.TriggerId, ct).ConfigureAwait(false);
        var wakesInHour = await WakesSinceAsync(tx, trigger.TargetAgentIds, now - TimeSpan.FromHours(1), ct).ConfigureAwait(false);
        return new RyuseiFireContext(targets, firesInDay, lastWoke, wakesInHour);
    }

    /// <summary>
    /// Appends one guard trip to <c>guard_event</c> (doc 06, doc 38). A ryusei trip has no GC, so <c>gc_id</c> is null and the
    /// trigger and fire go in <paramref name="counters"/>.
    /// </summary>
    public static async Task InsertGuardEventAsync(
        NpgsqlTransaction tx,
        string kind,
        JsonObject counters,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(counters);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO guard_event (kind, counters, created_at)
            VALUES (@kind, @counters::jsonb, @at)
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("counters", counters.ToJsonString());
        cmd.Parameters.AddWithValue("at", at);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Counts the woke fires of a trigger made after <paramref name="since"/>. The daily cap reads it.</summary>
    public static async Task<int> CountWokeSinceAsync(NpgsqlTransaction tx, Guid triggerId, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM ryusei_fire
            WHERE trigger_id = @trigger_id AND outcome = 'woke' AND at > @since
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("trigger_id", triggerId);
        cmd.Parameters.AddWithValue("since", since);
        return checked((int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    /// <summary>The time of the latest woke fire of a trigger, or null when it has never woken anyone. The cooldown reads it.</summary>
    public static async Task<DateTimeOffset?> LastWokeAtAsync(NpgsqlTransaction tx, Guid triggerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = new NpgsqlCommand("""
            SELECT max(at) FROM ryusei_fire WHERE trigger_id = @trigger_id AND outcome = 'woke'
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("trigger_id", triggerId);

        // A reader, not ExecuteScalar: scalar reads of timestamptz come back as DateTime, not DateTimeOffset.
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>
    /// The highest count of inbox items made for any one of <paramref name="agentIds"/> since <paramref name="since"/>, which is
    /// the shared wake count per agent (doc 06, doc 38). Zero when there are no agents or no wakes. The wake cap reads it.
    /// </summary>
    public static async Task<int> WakesSinceAsync(
        NpgsqlTransaction tx,
        IReadOnlyCollection<Guid> agentIds,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(agentIds);
        if (agentIds.Count == 0)
        {
            return 0;
        }

        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM inbox_item
            WHERE agent_id = ANY(@agent_ids) AND created_at >= @since
            GROUP BY agent_id
            ORDER BY count(*) DESC
            LIMIT 1
            """, tx.Connection, tx);
        cmd.Parameters.AddWithValue("agent_ids", agentIds.ToArray());
        cmd.Parameters.AddWithValue("since", since);
        var top = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return top is null or DBNull ? 0 : checked((int)(long)top);
    }

    /// <summary>The <c>agent.state</c> of each id that exists. A missing id is absent from the result.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> AgentStatesAsync(
        NpgsqlTransaction tx,
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(agentIds);
        var states = new Dictionary<Guid, string>();
        if (agentIds.Count == 0)
        {
            return states;
        }

        await using var cmd = new NpgsqlCommand("SELECT agent_id, state FROM agent WHERE agent_id = ANY(@agent_ids)", tx.Connection, tx);
        cmd.Parameters.AddWithValue("agent_ids", agentIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            states[reader.GetGuid(0)] = reader.GetString(1);
        }

        return states;
    }
}
