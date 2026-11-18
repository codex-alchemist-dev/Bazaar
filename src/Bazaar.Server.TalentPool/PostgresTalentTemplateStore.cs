using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.TalentPool;

/// <summary>
/// <see cref="ITalentTemplateStore"/> over <c>talent_templates</c> (migration talent_pool/0001). The next version number is computed in the
/// INSERT itself; a race on it or on the content-hash unique key surfaces as a unique violation and is retried, so two submitters never share
/// a version.
/// </summary>
public sealed class PostgresTalentTemplateStore(NpgsqlDataSource dataSource) : ITalentTemplateStore
{
    private const string _columns = "template_id, version, manifest::text, content_sha, status, source_soul_id";
    private const string _byHash = "SELECT " + _columns + " FROM talent_templates WHERE template_id = $1 AND content_sha = $2";
    private const string _insert =
        "INSERT INTO talent_templates (template_id, version, manifest, content_sha, source_soul_id, submitted_by) " +
        "SELECT $1::uuid, COALESCE(MAX(version), 0) + 1, $2::jsonb, $3::text, $4::uuid, $5::text FROM talent_templates WHERE template_id = $1 " +
        "RETURNING " + _columns;
    private const string _get = "SELECT " + _columns + " FROM talent_templates WHERE template_id = $1 AND version = $2";
    private const string _list = "SELECT " + _columns + " FROM talent_templates WHERE template_id = $1 ORDER BY version";
    private const string _resolve =
        "UPDATE talent_templates SET status = $3, status_changed_at = now() WHERE template_id = $1 AND version = $2 AND status = 'quarantined'";
    private const int _attempts = 32;

    public async Task<TalentTemplateAddResult> AddVersionAsync(
        Guid templateId, ValidatedTalentManifest manifest, Guid? sourceSoulId, string submittedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedBy);
        // The hash is the identity of the content; never store a manifest under a hash that is not its own.
        if (TalentManifestJson.Sha(manifest.Manifest) != manifest.ContentSha)
            throw new ArgumentException("The content hash does not match the manifest.", nameof(manifest));
        var json = TalentManifestJson.Write(manifest.Manifest);

        for (var attempt = 1; ; attempt++)
        {
            var existing = await ReadOneAsync(_byHash, templateId, manifest.ContentSha, ct).ConfigureAwait(false);
            if (existing is not null)
                return new TalentTemplateAddResult(existing, false);

            await using var cmd = dataSource.CreateCommand(_insert);
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = templateId });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = json, NpgsqlDbType = NpgsqlDbType.Jsonb });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = manifest.ContentSha });
            cmd.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = sourceSoulId });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = submittedBy });
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    return new TalentTemplateAddResult(Map(reader), true);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && attempt < _attempts)
            {
                // Lost a race for the version number or the same content landed first: look again.
            }
        }
    }

    public Task<TalentTemplate?> GetAsync(Guid templateId, int version, CancellationToken ct) =>
        ReadOneAsync(_get, templateId, version, ct);

    public async Task<IReadOnlyList<TalentTemplate>> ListVersionsAsync(Guid templateId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_list);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = templateId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<TalentTemplate>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(Map(reader));
        return result;
    }

    public async Task<bool> TryResolveAsync(Guid templateId, int version, TalentTemplateStatus newStatus, CancellationToken ct)
    {
        if (newStatus == TalentTemplateStatus.Quarantined)
            throw new ArgumentException("A version cannot be moved back into quarantine.", nameof(newStatus));

        await using var cmd = dataSource.CreateCommand(_resolve);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = templateId });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = version });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ToDb(newStatus) });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private async Task<TalentTemplate?> ReadOneAsync(string sql, Guid templateId, object second, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = templateId });
        cmd.Parameters.Add(second switch
        {
            int v => new NpgsqlParameter<int> { TypedValue = v },
            string s => new NpgsqlParameter<string> { TypedValue = s },
            _ => throw new ArgumentException("Unsupported parameter.", nameof(second)),
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static TalentTemplate Map(NpgsqlDataReader reader)
    {
        var templateId = reader.GetGuid(0);
        return new TalentTemplate(
            templateId,
            reader.GetInt32(1),
            TalentManifestJson.Read(reader.GetString(2), templateId),
            reader.GetString(3),
            FromDb(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetGuid(5));
    }

    private static string ToDb(TalentTemplateStatus status) => status switch
    {
        TalentTemplateStatus.Quarantined => "quarantined",
        TalentTemplateStatus.Hired => "hired",
        TalentTemplateStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static TalentTemplateStatus FromDb(string value) => value switch
    {
        "quarantined" => TalentTemplateStatus.Quarantined,
        "hired" => TalentTemplateStatus.Hired,
        "rejected" => TalentTemplateStatus.Rejected,
        _ => throw new InvalidDataException($"Unknown talent template status '{value}'."),
    };
}
