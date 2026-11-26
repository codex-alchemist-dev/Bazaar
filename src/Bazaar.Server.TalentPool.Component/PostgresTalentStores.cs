using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Npgsql;

namespace Bazaar.Server.TalentPool.Component;

/// <summary>Postgres template store (<c>talent_template</c>).</summary>
public sealed class PostgresTalentTemplateStore(NpgsqlDataSource dataSource) : ITalentTemplateStore
{
    private const string _columns = "template_id, version, manifest, content_sha, submitted_by, submitted_at";

    /// <inheritdoc />
    public async Task AddAsync(TalentTemplate version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO talent_template (template_id, version, manifest, content_sha, submitted_by, submitted_at) VALUES ($1, $2, $3, $4, $5, $6)");
        cmd.Parameters.AddWithValue(version.TemplateId);
        cmd.Parameters.AddWithValue(version.Version);
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(version.Manifest, TalentJson.Default.SoulManifest), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        cmd.Parameters.AddWithValue(version.ContentSha);
        cmd.Parameters.AddWithValue(version.SubmittedBy);
        cmd.Parameters.AddWithValue(version.SubmittedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TalentTemplate?> FindAsync(string templateId, int? version, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM talent_template WHERE template_id = $1 AND ($2::int IS NULL OR version = $2) ORDER BY version DESC LIMIT 1");
        cmd.Parameters.AddWithValue(templateId);
        cmd.Parameters.Add(new NpgsqlParameter<int?> { TypedValue = version });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TalentTemplate>> ListLatestAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT DISTINCT ON (template_id) {_columns} FROM talent_template ORDER BY template_id, version DESC");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TalentTemplate>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(reader));
        }

        return list;
    }

    private static TalentTemplate Read(NpgsqlDataReader r) => new(
        r.GetString(0),
        r.GetInt32(1),
        JsonSerializer.Deserialize(r.GetString(2), TalentJson.Default.SoulManifest)!,
        r.GetString(3),
        r.GetString(4),
        r.GetFieldValue<DateTimeOffset>(5));
}

/// <summary>Postgres store for the Council hire state (<c>talent_hire_state</c>), the owning store of <see cref="IHireDecisionStore"/>.</summary>
public sealed class PostgresHireDecisionStore(NpgsqlDataSource dataSource) : IHireDecisionStore
{
    /// <inheritdoc />
    public async Task<HireVoteState?> FindAsync(string templateId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT hire_key, vote_id, status, decision, updated_at FROM talent_hire_state WHERE hire_key = $1");
        cmd.Parameters.AddWithValue(templateId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new HireVoteState(
            r.GetString(0),
            r.GetGuid(1),
            Enum.Parse<HireStatus>(r.GetString(2), ignoreCase: true),
            r.IsDBNull(3) ? null : Enum.Parse<MembershipDecision>(r.GetString(3), ignoreCase: true),
            r.GetFieldValue<DateTimeOffset>(4));
    }

    /// <inheritdoc />
    public async Task SaveAsync(HireVoteState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO talent_hire_state (hire_key, vote_id, status, decision, updated_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (hire_key) DO UPDATE
            SET vote_id = EXCLUDED.vote_id, status = EXCLUDED.status, decision = EXCLUDED.decision, updated_at = EXCLUDED.updated_at
            """);
        cmd.Parameters.AddWithValue(state.TemplateId);
        cmd.Parameters.AddWithValue(state.VoteId);
        cmd.Parameters.AddWithValue(state.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue((object?)state.Decision?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue(state.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Tier 2 tool-grant approvals (<c>talent_tool_grant_approval</c>). The owner's approval flow calls <see cref="RecordAsync"/>;
/// the Talent Pool only reads. A hire vote never records one.
/// </summary>
public sealed class PostgresToolGrantApproval(NpgsqlDataSource dataSource, TimeProvider time) : ITalentToolGrantApproval
public sealed class PostgresToolGrantApproval(NpgsqlDataSource dataSource, TimeProvider time) : ITalentToolGrantApproval, IToolGrantRecorder
{
    /// <summary>Lowercase hex SHA-256 of the sorted tool list, so order does not matter and any change needs a new approval.</summary>
    public static string ToolsSha(IEnumerable<string> tools) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tools.Order(StringComparer.Ordinal)))));

    /// <inheritdoc />
    public async Task<bool> IsApprovedAsync(string hireKey, IReadOnlyList<string> tools, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM talent_tool_grant_approval WHERE hire_key = $1 AND tools_sha = $2)");
        cmd.Parameters.AddWithValue(hireKey);
        cmd.Parameters.AddWithValue(ToolsSha(tools));
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Records the owner's approval of exactly this tool list for a template version. Idempotent.</summary>
    public async Task RecordAsync(string hireKey, IReadOnlyList<string> tools, string approvedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hireKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO talent_tool_grant_approval (hire_key, tools_sha, approved_by, approved_at) VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue(hireKey);
        cmd.Parameters.AddWithValue(ToolsSha(tools));
        cmd.Parameters.AddWithValue(approvedBy);
        cmd.Parameters.AddWithValue(time.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Records an owner-approved tool grant. Implemented over Postgres; a test double stands in for it in unit tests.</summary>
public interface IToolGrantRecorder
{
    /// <summary>Records the owner's approval of exactly this tool list for a template version. Idempotent.</summary>
    Task RecordAsync(string hireKey, IReadOnlyList<string> tools, string approvedBy, CancellationToken ct);
}
