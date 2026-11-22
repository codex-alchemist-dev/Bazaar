using System.Text;
using System.Text.Json;
using Npgsql;

namespace Bazaar.Server.Mirror;

/// <summary>SQL-first Mirror storage (doc 26 section 5). Signal payloads and profile bodies are sealed with <see cref="IMirrorCipher"/>.</summary>
public sealed class PostgresMirrorStore(NpgsqlDataSource dataSource, IMirrorCipher cipher) : IMirrorStore
{
    private static byte[] SignalAd(Guid id) => Encoding.UTF8.GetBytes("mirror_signal:" + id.ToString("N"));
    private static byte[] ProfileAd(int version) => Encoding.UTF8.GetBytes("mirror_profile:" + version);

    public async Task AddSignalAsync(MirrorSignal signal, CancellationToken cancellationToken)
    {
        var sealedValue = cipher.Seal(signal.Payload, SignalAd(signal.Id));
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO mirror_signal (id, kind, topic, direction, source_ref, weight, key_id, nonce, ciphertext, created_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)");
        cmd.Parameters.AddWithValue(signal.Id);
        cmd.Parameters.AddWithValue(signal.Kind.ToString());
        cmd.Parameters.AddWithValue(signal.Topic);
        cmd.Parameters.AddWithValue(signal.Direction.ToString());
        cmd.Parameters.AddWithValue(signal.SourceRef);
        cmd.Parameters.AddWithValue(signal.Weight);
        cmd.Parameters.AddWithValue(sealedValue.KeyId);
        cmd.Parameters.AddWithValue(sealedValue.Nonce);
        cmd.Parameters.AddWithValue(sealedValue.Ciphertext);
        cmd.Parameters.AddWithValue(signal.CreatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MirrorSignal>> ListSignalsAsync(CancellationToken cancellationToken)
    {
        var list = new List<MirrorSignal>();
        await using var cmd = dataSource.CreateCommand(
            "SELECT id, kind, topic, direction, source_ref, weight, key_id, nonce, ciphertext, created_at FROM mirror_signal ORDER BY created_at, id");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            var payload = cipher.Open(new SealedMirrorValue(reader.GetGuid(6), reader.GetFieldValue<byte[]>(7), reader.GetFieldValue<byte[]>(8)), SignalAd(id));
            list.Add(new MirrorSignal(
                id,
                Enum.Parse<MirrorSignalKind>(reader.GetString(1)),
                reader.GetString(2),
                Enum.Parse<MirrorDirection>(reader.GetString(3)),
                reader.GetString(4),
                reader.GetDouble(5),
                payload,
                reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return list;
    }

    public async Task AddProfileAsync(MirrorProfile profile, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(profile.Rules.Select(r => new RuleDto(r.Topic, r.Direction.ToString(), r.Count, [.. r.EvidenceRefs])).ToList(), MirrorJson.Default.ListRuleDto);
        var sealedValue = cipher.Seal(body, ProfileAd(profile.Version));
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO mirror_profile (version, key_id, nonce, ciphertext, created_at) VALUES ($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(profile.Version);
        cmd.Parameters.AddWithValue(sealedValue.KeyId);
        cmd.Parameters.AddWithValue(sealedValue.Nonce);
        cmd.Parameters.AddWithValue(sealedValue.Ciphertext);
        cmd.Parameters.AddWithValue(profile.CreatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MirrorProfile?> GetLatestProfileAsync(CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT version, key_id, nonce, ciphertext, created_at FROM mirror_profile ORDER BY version DESC LIMIT 1");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var version = reader.GetInt32(0);
        var body = cipher.Open(new SealedMirrorValue(reader.GetGuid(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3)), ProfileAd(version));
        var rules = (JsonSerializer.Deserialize(body, MirrorJson.Default.ListRuleDto) ?? [])
            .Select(r => new MirrorRule(r.Topic, Enum.Parse<MirrorDirection>(r.Direction), r.Count, r.EvidenceRefs))
            .ToList();
        return new MirrorProfile(version, rules, reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task<bool> GetPausedAsync(CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT paused FROM mirror_state WHERE id = 1");
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO mirror_state (id, paused) VALUES (1, $1) ON CONFLICT (id) DO UPDATE SET paused = EXCLUDED.paused");
        cmd.Parameters.AddWithValue(paused);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> ListSuppressedAsync(CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = dataSource.CreateCommand("SELECT rule_key FROM mirror_suppressed");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    public async Task AddSuppressedAsync(string key, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("INSERT INTO mirror_suppressed (rule_key) VALUES ($1) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue(key);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WipeAsync(CancellationToken cancellationToken)
    {
        // One transaction: a wipe is all or nothing. The paused flag is a setting, so it stays.
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in new[] { "mirror_signal", "mirror_profile", "mirror_suppressed" })
        {
            await using var cmd = new NpgsqlCommand($"DELETE FROM {table}", conn, tx);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal sealed record RuleDto(string Topic, string Direction, int Count, List<string> EvidenceRefs);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<PostgresMirrorStore.RuleDto>))]
internal sealed partial class MirrorJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
