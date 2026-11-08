using System.Text.Json;
using Npgsql;

namespace Bazaar.Server.Community.Policy;

/// <summary>
/// Loads the <c>platform_policy</c> table (migration 0001, community) through the shared <see cref="NpgsqlDataSource"/>.
/// Raw Npgsql and SQL-first, as the rest of the server. A row that does not parse throws <see cref="InvalidDataException"/>
/// rather than being skipped, so a bad record can never widen what is allowed.
/// </summary>
public sealed class PlatformPolicyLoader(NpgsqlDataSource dataSource)
{
    /// <summary>Every row, ordered by platform. Parameter-free, so it can be read and checked without a database.</summary>
    public const string SelectAllSql =
        "SELECT platform, status, allowed_actions::text, disclosure_rule, source_url, checked_on FROM platform_policy ORDER BY platform";

    /// <summary>Reads every policy row. An empty table returns an empty list; see <see cref="LoadOrSeedAsync"/> for the fallback.</summary>
    public async Task<IReadOnlyList<PlatformPolicy>> LoadAllAsync(CancellationToken ct = default)
    {
        var rows = new List<PlatformPolicy>();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectAllSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new PlatformPolicy(
                reader.GetString(0),
                ParseStatus(reader.GetString(1)),
                ParseAllowedActions(reader.GetString(2)),
                reader.GetString(3),
                new Uri(reader.GetString(4), UriKind.Absolute),
                reader.GetFieldValue<DateOnly>(5)));
        }

        return rows;
    }

    /// <summary>
    /// Reads the table, or returns <see cref="PlatformPolicySeed.All"/> when the table is empty (a fresh install before the
    /// seed is written). A read error is not caught: the caller must not fall back silently when the database is unreachable.
    /// </summary>
    public async Task<IReadOnlyList<PlatformPolicy>> LoadOrSeedAsync(CancellationToken ct = default)
    {
        var rows = await LoadAllAsync(ct).ConfigureAwait(false);
        return rows.Count == 0 ? PlatformPolicySeed.All : rows;
    }

    /// <summary>Maps the stored status text to the enum. Unknown text throws, matching the table's CHECK constraint.</summary>
    public static PolicyStatus ParseStatus(string status) => status switch
    {
        "Verified" => PolicyStatus.Verified,
        "Secondary" => PolicyStatus.Secondary,
        "Unverified" => PolicyStatus.Unverified,
        "Banned" => PolicyStatus.Banned,
        _ => throw new InvalidDataException($"Unknown platform_policy status '{status}'."),
    };

    /// <summary>Parses the jsonb array of action names. Any element that is not a defined <see cref="CommunityAction"/> throws.</summary>
    public static IReadOnlySet<CommunityAction> ParseAllowedActions(string json)
    {
        var actions = new HashSet<CommunityAction>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("platform_policy.allowed_actions must be a JSON array.");
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || !Enum.TryParse(element.GetString(), ignoreCase: false, out CommunityAction action)
                    || !Enum.IsDefined(action))
                {
                    throw new InvalidDataException($"Unknown community action in platform_policy.allowed_actions: {element.GetRawText()}.");
                }

                actions.Add(action);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("platform_policy.allowed_actions is not valid JSON.", ex);
        }

        return actions;
    }
}
