using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Policy;
using Npgsql;

namespace Bazaar.Server.Gateway.Data;

/// <summary>Providers, models and routes (<c>gw_provider</c>, <c>gw_model</c>, <c>gw_route</c>). Also the resolver's read side.</summary>
public sealed class GatewayDirectoryRepository(NpgsqlDataSource dataSource) : IGatewayDirectory
{
    public async ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT id, kind, adapter, base_url, auth_kind, secret_ref, headers::text, proxy_ref, terms_ack_at, enabled FROM gw_provider WHERE id = $1");
        cmd.Parameters.AddWithValue(providerId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProvider(reader) : null;
    }

    public async Task<IReadOnlyList<GwProvider>> ListProvidersAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT id, kind, adapter, base_url, auth_kind, secret_ref, headers::text, proxy_ref, terms_ack_at, enabled FROM gw_provider ORDER BY id");
        var result = new List<GwProvider>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadProvider(reader));
        }

        return result;
    }

    /// <summary>Inserts or replaces a provider. The table's CHECKs refuse an auth kind other than api_key, bearer or none.</summary>
    public async Task UpsertProviderAsync(GwProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO gw_provider (id, kind, adapter, base_url, auth_kind, secret_ref, headers, proxy_ref, terms_ack_at, enabled)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (id) DO UPDATE SET kind = EXCLUDED.kind, adapter = EXCLUDED.adapter, base_url = EXCLUDED.base_url,
                auth_kind = EXCLUDED.auth_kind, secret_ref = EXCLUDED.secret_ref, headers = EXCLUDED.headers,
                proxy_ref = EXCLUDED.proxy_ref, terms_ack_at = EXCLUDED.terms_ack_at, enabled = EXCLUDED.enabled
            """);
        cmd.Parameters.AddWithValue(provider.Id);
        cmd.Parameters.AddWithValue(provider.Kind);
        cmd.Parameters.AddWithValue(provider.Adapter);
        cmd.Parameters.AddWithValue(provider.BaseUrl);
        cmd.Parameters.AddWithValue(provider.AuthKind);
        cmd.Parameters.AddWithValue((object?)provider.SecretRef ?? DBNull.Value);
        cmd.Parameters.Add(GatewayJson.Jsonb(provider.HeadersJson));
        cmd.Parameters.AddWithValue((object?)provider.ProxyRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)provider.TermsAckAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue(provider.Enabled);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT provider_id, model_id, family, identity, caps::text, status, first_seen, last_seen, source FROM gw_model WHERE provider_id = $1 AND model_id = $2");
        cmd.Parameters.AddWithValue(model.ProviderId);
        cmd.Parameters.AddWithValue(model.ModelId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadModel(reader) : null;
    }

    public async Task<IReadOnlyList<GwModel>> ListModelsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT provider_id, model_id, family, identity, caps::text, status, first_seen, last_seen, source FROM gw_model WHERE provider_id = $1 ORDER BY model_id");
        cmd.Parameters.AddWithValue(providerId);
        var result = new List<GwModel>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadModel(reader));
        }

        return result;
    }

    /// <summary>Inserts a model, or refreshes everything but <c>first_seen</c> on conflict. Never touches any agent policy.</summary>
    public async Task UpsertModelAsync(GwModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO gw_model (provider_id, model_id, family, identity, caps, status, first_seen, last_seen, source)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (provider_id, model_id) DO UPDATE SET family = EXCLUDED.family, identity = EXCLUDED.identity,
                caps = EXCLUDED.caps, status = EXCLUDED.status, last_seen = EXCLUDED.last_seen, source = EXCLUDED.source
            """);
        cmd.Parameters.AddWithValue(model.ProviderId);
        cmd.Parameters.AddWithValue(model.ModelId);
        cmd.Parameters.AddWithValue(model.Family);
        cmd.Parameters.AddWithValue(model.Identity == GatewayIdentity.Native ? "native" : "pooled");
        cmd.Parameters.Add(GatewayJson.Jsonb(model.CapsJson));
        cmd.Parameters.AddWithValue(model.Status);
        cmd.Parameters.AddWithValue(model.FirstSeen);
        cmd.Parameters.AddWithValue(model.LastSeen);
        cmd.Parameters.AddWithValue(model.Source);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id, strategy, members::text, expected_set::text FROM gw_route WHERE id = $1");
        cmd.Parameters.AddWithValue(routeId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new GwRoute(
            reader.GetString(0),
            ParseStrategy(reader.GetString(1)),
            GatewayJson.ReadModelRefs(reader.GetString(2)),
            GatewayJson.ReadStrings(reader.GetString(3)));
    }

    /// <summary>Inserts or replaces a route. A route id must be a valid route name (no wildcard, no <c>auto</c>).</summary>
    public async Task UpsertRouteAsync(GwRoute route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!RouteNames.IsValid(route.Id))
        {
            throw new ArgumentException($"'{route.Id}' is not a valid route name.", nameof(route));
        }

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO gw_route (id, strategy, members, expected_set) VALUES ($1, $2, $3, $4)
            ON CONFLICT (id) DO UPDATE SET strategy = EXCLUDED.strategy, members = EXCLUDED.members, expected_set = EXCLUDED.expected_set
            """);
        cmd.Parameters.AddWithValue(route.Id);
        cmd.Parameters.AddWithValue(FormatStrategy(route.Strategy));
        cmd.Parameters.Add(GatewayJson.Jsonb(GatewayJson.Array(route.Members.Select(m => m.ToString()))));
        cmd.Parameters.Add(GatewayJson.Jsonb(GatewayJson.Array(route.ExpectedSet)));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string FormatStrategy(RouteStrategy s) => s switch
    {
        RouteStrategy.Priority => "priority",
        RouteStrategy.RoundRobin => "round_robin",
        RouteStrategy.LeastLoaded => "least_loaded",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static RouteStrategy ParseStrategy(string s) => s switch
    {
        "priority" => RouteStrategy.Priority,
        "round_robin" => RouteStrategy.RoundRobin,
        "least_loaded" => RouteStrategy.LeastLoaded,
        _ => throw new InvalidOperationException($"Unknown route strategy '{s}'."),
    };

    private static GwProvider ReadProvider(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetBoolean(9));

    private static GwModel ReadModel(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetString(3) == "native" ? GatewayIdentity.Native : GatewayIdentity.Pooled,
        r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7), r.GetString(8));
}
