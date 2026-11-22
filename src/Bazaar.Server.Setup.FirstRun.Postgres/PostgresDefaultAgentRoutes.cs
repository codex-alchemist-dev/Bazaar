using System.Text.Json.Nodes;
using Bazaar.Server.Gateway.Policy;
using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>
/// The default agent's routes (doc 05, doc 91). Reads only what the gateway lists: the available models of enabled providers whose
/// model id or family is a Claude one, optionally narrowed to the requested source (provider) ids. It then upserts one priority
/// route over those models and returns the route name followed by each provider-qualified model, so the agent's policy allows the
/// route and exactly the models behind it. No Claude source means an empty list and no route. A source id the gateway does not list
/// yields nothing, never a made-up entry.
/// </summary>
/// <remarks>
/// The selection profile (doc 92) is validated against the known names but not stored: the doc is not in the tree and the route
/// table has no column for it, so the strategy is <c>priority</c> in provider-id order until it lands.
/// </remarks>
public sealed class PostgresDefaultAgentRoutes(NpgsqlDataSource dataSource) : IDefaultAgentRoutes
{
    /// <summary>The route the seed creates for the default agent. A valid route name: no colon, no wildcard, not <c>auto</c>.</summary>
    public const string RouteName = "claude-first-agent";

    private static readonly string[] _profiles = ["balanced", "latency", "reliability", "cost", "quality"];

    private const string _modelsSql = """
        SELECT m.provider_id, m.model_id
        FROM gw_model m
        JOIN gw_provider p ON p.id = m.provider_id
        WHERE p.enabled AND m.status = 'available'
          AND (m.model_id ILIKE '%claude%' OR m.family ILIKE '%claude%')
          AND (cardinality(@sources) = 0 OR m.provider_id = ANY(@sources))
        ORDER BY m.provider_id, m.model_id
        """;

    private const string _routeSql = """
        INSERT INTO gw_route (id, strategy, members, expected_set) VALUES (@id, 'priority', @members::jsonb, '[]'::jsonb)
        ON CONFLICT (id) DO UPDATE SET members = EXCLUDED.members
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetWhitelistAsync(IReadOnlyList<string> requestedSources, string profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedSources);
        if (!_profiles.Contains(profile, StringComparer.Ordinal))
            throw new ArgumentException("The selection profile is not one of balanced, latency, reliability, cost or quality.", nameof(profile));

        var models = new List<string>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var read = new NpgsqlCommand(_modelsSql, connection))
        {
            read.Parameters.AddWithValue("sources", requestedSources.ToArray());
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var entry = $"{reader.GetString(0)}:{reader.GetString(1)}";
                if (ModelRef.TryParse(entry, out _)) models.Add(entry);
            }
        }

        if (models.Count == 0) return [];

        await using var upsert = new NpgsqlCommand(_routeSql, connection);
        upsert.Parameters.AddWithValue("id", RouteName);
        upsert.Parameters.AddWithValue("members", new JsonArray([.. models.Select(model => (JsonNode)JsonValue.Create(model)!)]).ToJsonString());
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return [RouteName, .. models];
    }
}
