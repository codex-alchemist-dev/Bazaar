using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.PsychWard.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.PsychWard;

/// <summary>JSON shapes for the check-in column, source-generated so the module stays AOT compatible.</summary>
[JsonSerializable(typeof(WardCheckIn))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class PsychWardJsonContext : JsonSerializerContext;

/// <summary>Postgres <see cref="IWardStayStore"/> (<c>ward_stay</c>, migration components/psych_ward/0001). One active stay per agent is enforced by a partial unique index.</summary>
public sealed class PgWardStayStore(NpgsqlDataSource dataSource) : IWardStayStore
{
    private const string _columns = "id, agent_id, trigger, detail, admitted_at, check_in, discharged_at, reviewer_id";

    /// <inheritdoc />
    public async Task<WardStay?> GetLatestAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM ward_stay WHERE agent_id = @agent ORDER BY admitted_at DESC LIMIT 1");
        cmd.Parameters.AddWithValue("agent", agentId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task SaveAsync(WardStay stay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stay);

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO ward_stay (id, agent_id, trigger, detail, admitted_at, check_in, discharged_at, reviewer_id)
            VALUES (@id, @agent, @trigger, @detail, @admitted, @check_in, @discharged, @reviewer)
            ON CONFLICT (id) DO UPDATE SET
                check_in = EXCLUDED.check_in,
                discharged_at = EXCLUDED.discharged_at,
                reviewer_id = EXCLUDED.reviewer_id
            """);
        cmd.Parameters.AddWithValue("id", stay.Id);
        cmd.Parameters.AddWithValue("agent", stay.AgentId);
        cmd.Parameters.AddWithValue("trigger", TriggerNames.ToName(stay.Trigger));
        cmd.Parameters.AddWithValue("detail", (object?)stay.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("admitted", stay.AdmittedAt);
        cmd.Parameters.Add(new NpgsqlParameter("check_in", NpgsqlDbType.Jsonb)
        {
            Value = stay.CheckIn is null ? DBNull.Value : JsonSerializer.Serialize(stay.CheckIn, PsychWardJsonContext.Default.WardCheckIn),
        });
        cmd.Parameters.AddWithValue("discharged", (object?)stay.DischargedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reviewer", (object?)stay.ReviewerId ?? DBNull.Value);

        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "ward_stay_one_active_per_agent")
        {
            throw new WardStayConflictException(stay.AgentId);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WardStay>> ListActiveAsync(CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM ward_stay WHERE discharged_at IS NULL ORDER BY admitted_at DESC");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var stays = new List<WardStay>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stays.Add(Read(reader));
        }

        return stays;
    }

    private static WardStay Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        TriggerNames.FromName(r.GetString(2)),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : JsonSerializer.Deserialize(r.GetString(5), PsychWardJsonContext.Default.WardCheckIn),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : r.GetGuid(7));
}

/// <summary>The snake_case names stored for each trigger.</summary>
internal static class TriggerNames
{
    public static string ToName(WardTrigger trigger) => trigger switch
    {
        WardTrigger.LoopGuard => "loop_guard",
        WardTrigger.RepeatedFailure => "repeated_failure",
        WardTrigger.DeniedCallOrCanary => "denied_call_or_canary",
        WardTrigger.TokenHardStop => "token_hard_stop",
        WardTrigger.SelfReferral => "self_referral",
        WardTrigger.OwnerReferral => "owner_referral",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
    };

    public static WardTrigger FromName(string name) => name switch
    {
        "loop_guard" => WardTrigger.LoopGuard,
        "repeated_failure" => WardTrigger.RepeatedFailure,
        "denied_call_or_canary" => WardTrigger.DeniedCallOrCanary,
        "token_hard_stop" => WardTrigger.TokenHardStop,
        "self_referral" => WardTrigger.SelfReferral,
        "owner_referral" => WardTrigger.OwnerReferral,
        _ => throw new InvalidOperationException($"Unknown ward trigger '{name}' in ward_stay."),
    };
}
