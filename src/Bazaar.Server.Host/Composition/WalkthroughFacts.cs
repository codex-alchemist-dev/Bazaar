using Bazaar.Server.Company;
using Bazaar.Server.Walkthrough;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>What the first-run seed made, and whether a person has posted in its chat (doc 45 sections 1.15 and 2.9).</summary>
/// <param name="FirstAgentId">The seeded default agent, or null when none was seeded.</param>
/// <param name="FirstAgentName">That agent's display name, or null.</param>
/// <param name="FirstChatId">The seeded general group chat, or null.</param>
public sealed record FirstRunSeedState(Guid? FirstAgentId, string? FirstAgentName, Guid? FirstChatId);

/// <summary>Reads the first-run seed markers and the first chat's authorship. Read-only.</summary>
public interface IFirstRunSeedReader
{
    /// <summary>The seeded agent and chat; both null before setup seeded them.</summary>
    Task<FirstRunSeedState> ReadSeedAsync(CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="personId"/> has authored a message in <paramref name="chatId"/>.</summary>
    Task<bool> HasPostedAsync(Guid chatId, Guid personId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The walkthrough's three facts from the server's own stores: the company profile (Company module), the seed markers (setup first-run)
/// and the first chat's authorship (the GC log). A first task is given when the person has written in the first chat. It never reads
/// message text, only who wrote.
/// </summary>
public sealed class SeededWalkthroughFactsProvider(ICompanyProfileReader company, IFirstRunSeedReader seed) : IWalkthroughFactsProvider
{
    public async Task<WalkthroughServerFacts> ReadAsync(string personId, CancellationToken cancellationToken = default)
    {
        var created = await company.GetAsync(cancellationToken).ConfigureAwait(false) is not null;
        var state = await seed.ReadSeedAsync(cancellationToken).ConfigureAwait(false);
        var given = state.FirstChatId is { } chat
            && Guid.TryParse(personId, out var person)
            && await seed.HasPostedAsync(chat, person, cancellationToken).ConfigureAwait(false);
        return new WalkthroughServerFacts(created, state.FirstAgentName, given, state.FirstAgentId, state.FirstChatId);
    }
}

/// <summary>Reads <c>firstrun_seed</c>, the agent row and <c>gc_message</c> with plain SQL.</summary>
public sealed class PostgresFirstRunSeedReader(NpgsqlDataSource dataSource) : IFirstRunSeedReader
{
    private const string _seedSql = """
        SELECT s.kind, s.entity_id, a.name
        FROM firstrun_seed s
        LEFT JOIN agent a ON s.kind = 'default_agent' AND a.agent_id = s.entity_id
        """;

    private const string _postedSql = "SELECT EXISTS (SELECT 1 FROM gc_message WHERE gc_id = @gc AND author_id = @author)";

    public async Task<FirstRunSeedState> ReadSeedAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_seedSql);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Guid? agentId = null, chatId = null;
        string? name = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(1);
            switch (reader.GetString(0))
            {
                case "default_agent":
                    agentId = id;
                    name = reader.IsDBNull(2) ? null : reader.GetString(2);
                    break;
                case "general_gc":
                    chatId = id;
                    break;
            }
        }

        return new FirstRunSeedState(agentId, name, chatId);
    }

    public async Task<bool> HasPostedAsync(Guid chatId, Guid personId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_postedSql);
        cmd.Parameters.AddWithValue("gc", chatId);
        cmd.Parameters.AddWithValue("author", personId);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
