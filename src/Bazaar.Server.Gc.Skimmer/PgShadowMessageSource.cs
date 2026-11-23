using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Skimmer.Rules;
using Bazaar.Server.Skimmer.Scoring;
using Bazaar.Server.Skimmer.Shadow;
using Npgsql;

namespace Bazaar.Server.Gc.Skimmer;

/// <summary>
/// The real <see cref="IShadowMessageSource"/> over the GC log (doc 13, "Restart": the log is the source of truth). It reads
/// <c>message</c> events after a seq in seq order, opens their first-version text through <see cref="IGcMessageTextSource"/>, and
/// builds the rules facts for one agent: a mention is a row in <c>gc_mention</c> (the validated registry, doc 01 WP8) and counts
/// when it names the agent; a DM is a <c>dm</c> GC the agent is an active member of; an owner message is by author kind.
/// </summary>
/// <remarks>
/// <see cref="GcMessageFacts.IsCritical"/> and <see cref="GcMessageFacts.SenderQuarantined"/> are false until their owners wire
/// them; in shadow mode nothing wakes, so a missing flag only lowers the measured recall (doc 13).
/// </remarks>
public sealed class PgShadowMessageSource(NpgsqlDataSource dataSource, IGcMessageTextSource texts, IShadowAgentDirectory directory) : IShadowMessageSource
{
    private const string _gcSql = """
        SELECT g.type_key = 'dm'
               AND EXISTS (SELECT 1 FROM gc_member_current m WHERE m.gc_id = g.gc_id AND m.principal_id = $2 AND m.left_seq IS NULL)
        FROM gc g WHERE g.gc_id = $1
        """;

    private const string _messagesSql = """
        SELECT e.seq, e.message_id, e.version, e.author_kind,
               coalesce((SELECT array_agg(mn.target_principal_id) FROM gc_mention mn WHERE mn.gc_id = e.gc_id AND mn.seq = e.seq), '{}')
        FROM gc_event e
        WHERE e.gc_id = $1 AND e.seq > $2 AND e.kind = 'message' AND e.message_id IS NOT NULL
        ORDER BY e.seq
        LIMIT $3
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShadowMessage>> ReadAfterAsync(Guid gcId, ShadowAgentInfo agent, long afterSeq, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var handles = (await directory.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(e => e.Agent.AgentId == agent.AgentId)?.Handles
            ?? new HashSet<string>();
        var agentHandles = new HashSet<string>(handles, StringComparer.OrdinalIgnoreCase) { agent.AgentId.ToString("D") };

        bool isDm;
        await using (var gcCmd = dataSource.CreateCommand(_gcSql))
        {
            gcCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
            gcCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agent.AgentId });
            isDm = await gcCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        }

        var rows = new List<(long Seq, Guid MessageId, int Version, string AuthorKind, Guid[] Mentions)>();
        await using (var cmd = dataSource.CreateCommand(_messagesSql))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = afterSeq });
            cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetFieldValue<Guid[]>(4)));
        }

        var result = new List<ShadowMessage>(rows.Count);
        foreach (var row in rows)
        {
            var text = await texts.GetMessageTextAsync(gcId, row.MessageId, row.Version, ct).ConfigureAwait(false) ?? string.Empty;
            var facts = new GcMessageFacts(
                gcId, row.Seq, agent.AgentId, agentHandles,
                [.. row.Mentions.Select(m => m.ToString("D"))],
                IsDmWithAgent: isDm,
                IsOwnerAuthor: row.AuthorKind == "owner",
                IsCritical: false,
                SenderQuarantined: false);
            result.Add(new ShadowMessage(new GcMessageRef(gcId, row.Seq, text, row.AuthorKind), facts));
        }

        return result;
    }
}
