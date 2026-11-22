using System.Text.Json;
using Bazaar.Server.Skimmer.Scoring;
using Npgsql;

namespace Bazaar.Server.Skimmer.Shadow;

/// <summary>
/// <see cref="IShadowAgentDirectory"/> over the <c>agent</c> table and <c>gate_config</c>: every active agent, with its name and its
/// id as mention handles and its subscriptions as the scoring profile (doc 13, "Placement"). An agent with no <c>gate_config</c>
/// row has no subscriptions, so only the rules stage can decide for it.
/// </summary>
public sealed class PgShadowAgentDirectory(NpgsqlDataSource dataSource) : IShadowAgentDirectory
{
    private const string _sql = """
        SELECT a.agent_id, a.name, g.subscriptions::text
        FROM agent a LEFT JOIN gate_config g ON g.agent_id = a.agent_id
        WHERE a.state = 'active'
        ORDER BY a.agent_id
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShadowAgentEntry>> ListAsync(CancellationToken ct)
    {
        var result = new List<ShadowAgentEntry>();
        await using var cmd = dataSource.CreateCommand(_sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            var name = reader.GetString(1);
            var subscriptions = reader.IsDBNull(2) ? [] : ParseSubscriptions(reader.GetString(2));
            result.Add(new ShadowAgentEntry(
                new ShadowAgentInfo(id, new AgentProfile(id, name, subscriptions)),
                new HashSet<string>([name, id.ToString("D")], StringComparer.OrdinalIgnoreCase)));
        }

        return result;
    }

    private static List<string> ParseSubscriptions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
    }
}
