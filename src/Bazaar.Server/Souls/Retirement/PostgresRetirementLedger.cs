using Bazaar.Contracts.Soul;
using Npgsql;

namespace Bazaar.Server.Souls.Retirement;

/// <summary>Durable PostgreSQL implementation of the retirement step ledger.</summary>
public sealed class PostgresRetirementLedger(NpgsqlDataSource dataSource) : IRetirementLedger
{
    public async Task<IReadOnlyList<RetirementStepResult>> ReadAsync(
        SoulRetirementSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT step, status, reason
            FROM soul_retirement_step
            WHERE soul_id = @soul_id AND agent_id = @agent_id
            ORDER BY step
            """);
        command.Parameters.AddWithValue("soul_id", subject.SoulId);
        command.Parameters.AddWithValue("agent_id", subject.AgentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<RetirementStepResult>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RetirementStepResult(
                ParseStep(reader.GetString(0)),
                ParseStatus(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return results;
    }

    public async Task RecordAsync(
        SoulRetirementSubject subject,
        RetirementStepResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO soul_retirement_step (soul_id, agent_id, step, status, reason)
            VALUES (@soul_id, @agent_id, @step, @status, @reason)
            ON CONFLICT (soul_id, agent_id, step) DO UPDATE
            SET status = EXCLUDED.status, reason = EXCLUDED.reason, updated_at = now()
            """);
        command.Parameters.AddWithValue("soul_id", subject.SoulId);
        command.Parameters.AddWithValue("agent_id", subject.AgentId);
        command.Parameters.AddWithValue("step", ToDb(result.Step));
        command.Parameters.AddWithValue("status", ToDb(result.Status));
        command.Parameters.AddWithValue("reason", (object?)result.Reason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToDb(RetirementStepKind step) => step switch
    {
        RetirementStepKind.PassportRevocation => "passport_revocation",
        RetirementStepKind.TokenRevocation => "token_revocation",
        RetirementStepKind.VmStop => "vm_stop",
        RetirementStepKind.VmChildDiskDelete => "vm_child_disk_delete",
        RetirementStepKind.VmParentDiskDelete => "vm_parent_disk_delete",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown retirement step."),
    };

    private static string ToDb(RetirementStepStatus status) => status switch
    {
        RetirementStepStatus.Done => "done",
        RetirementStepStatus.AlreadyDone => "already_done",
        RetirementStepStatus.Failed => "failed",
        RetirementStepStatus.Deferred => "deferred",
        RetirementStepStatus.Refused => "refused",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown retirement status."),
    };

    private static RetirementStepKind ParseStep(string value) => value switch
    {
        "passport_revocation" => RetirementStepKind.PassportRevocation,
        "token_revocation" => RetirementStepKind.TokenRevocation,
        "vm_stop" => RetirementStepKind.VmStop,
        "vm_child_disk_delete" => RetirementStepKind.VmChildDiskDelete,
        "vm_parent_disk_delete" => RetirementStepKind.VmParentDiskDelete,
        _ => throw new InvalidDataException($"Unknown retirement step '{value}'."),
    };

    private static RetirementStepStatus ParseStatus(string value) => value switch
    {
        "done" => RetirementStepStatus.Done,
        "already_done" => RetirementStepStatus.AlreadyDone,
        "failed" => RetirementStepStatus.Failed,
        "deferred" => RetirementStepStatus.Deferred,
        "refused" => RetirementStepStatus.Refused,
        _ => throw new InvalidDataException($"Unknown retirement status '{value}'."),
    };
}
