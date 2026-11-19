using Npgsql;

namespace Bazaar.Server.Mail.Quarantine;

/// <summary>Merging rules shared by every <see cref="IProtectedMailDomains"/> implementation.</summary>
internal static class ProtectedDomainSet
{
    public static IReadOnlyList<string> Merge(IEnumerable<string> operatorDomains, IReadOnlyCollection<string> extras)
    {
        ArgumentNullException.ThrowIfNull(extras);
        return [.. operatorDomains.Concat(extras)
            .Select(d => d?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }
}

/// <summary>
/// Reads the operator's domains from <c>mail_domain.domain</c> (mail 0021). Every row counts whatever its state: a retired domain is
/// still a name worth guarding against imitation. Raw Npgsql on the shared data source; no domain is hardcoded.
/// </summary>
public sealed class PostgresProtectedMailDomains(NpgsqlDataSource dataSource) : IProtectedMailDomains
{
    private const string _select = "SELECT domain FROM mail_domain";

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAsync(IReadOnlyCollection<string> extraDomains, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extraDomains);
        var domains = new List<string>();
        await using var cmd = dataSource.CreateCommand(_select);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            domains.Add(reader.GetString(0));
        }

        return ProtectedDomainSet.Merge(domains, extraDomains);
    }
}

/// <summary>In-memory <see cref="IProtectedMailDomains"/> for tests and for hosts with a fixed domain list.</summary>
public sealed class InMemoryProtectedMailDomains(IEnumerable<string>? operatorDomains = null) : IProtectedMailDomains
{
    private readonly string[] _domains = [.. operatorDomains ?? []];

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetAsync(IReadOnlyCollection<string> extraDomains, CancellationToken ct = default) =>
        Task.FromResult(ProtectedDomainSet.Merge(_domains, extraDomains));
}
