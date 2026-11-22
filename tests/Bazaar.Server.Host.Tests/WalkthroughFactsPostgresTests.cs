using Bazaar.Server.Host.Composition;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// The first-run seed reader and the agent identity reader against real PostgreSQL. The tables are made here with only the columns the
/// queries touch, so the test runs on a plain database; the real columns come from the migrations the other suites cover.
/// </summary>
public sealed class WalkthroughFactsPostgresTests(HostDb db) : IClassFixture<HostDb>
{
    private async Task<NpgsqlDataSource> CreateAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var source = NpgsqlDataSource.Create(db.ConnectionString);
        var schema = "t" + Guid.NewGuid().ToString("N");
        await using (var cmd = source.CreateCommand($"""
            CREATE SCHEMA {schema};
            SET search_path TO {schema};
            """))
            await cmd.ExecuteNonQueryAsync(ct);
        await source.DisposeAsync();

        var builder = new NpgsqlConnectionStringBuilder(db.ConnectionString) { SearchPath = schema };
        var scoped = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var create = scoped.CreateCommand("""
            CREATE TABLE firstrun_seed (kind text PRIMARY KEY, entity_id uuid NOT NULL, detail jsonb NOT NULL DEFAULT '{}'::jsonb);
            CREATE TABLE agent (agent_id uuid PRIMARY KEY DEFAULT gen_random_uuid(), name text NOT NULL, role text NULL);
            CREATE TABLE gc_message (gc_id uuid NOT NULL, message_id uuid NOT NULL DEFAULT gen_random_uuid(), author_id uuid NOT NULL);
            """);
        await create.ExecuteNonQueryAsync(ct);
        return scoped;
    }

    [Fact]
    public async Task Nothing_seeded_reads_as_no_agent_and_no_chat()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = await CreateAsync(ct);

        Assert.Equal(new FirstRunSeedState(null, null, null), await new PostgresFirstRunSeedReader(source).ReadSeedAsync(ct));
    }

    [Fact]
    public async Task The_seed_markers_give_the_agent_with_its_name_and_the_chat_and_authorship_is_per_person()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = await CreateAsync(ct);
        var agent = Guid.NewGuid();
        var chat = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await using (var seed = source.CreateCommand("""
            INSERT INTO agent (agent_id, name) VALUES (@agent, 'Claude');
            INSERT INTO firstrun_seed (kind, entity_id) VALUES ('default_agent', @agent), ('general_gc', @chat);
            INSERT INTO gc_message (gc_id, author_id) VALUES (@chat, @agent);
            """))
        {
            seed.Parameters.AddWithValue("agent", agent);
            seed.Parameters.AddWithValue("chat", chat);
            await seed.ExecuteNonQueryAsync(ct);
        }

        var reader = new PostgresFirstRunSeedReader(source);
        Assert.Equal(new FirstRunSeedState(agent, "Claude", chat), await reader.ReadSeedAsync(ct));
        Assert.False(await reader.HasPostedAsync(chat, owner, ct));

        await using (var post = source.CreateCommand("INSERT INTO gc_message (gc_id, author_id) VALUES (@chat, @owner)"))
        {
            post.Parameters.AddWithValue("chat", chat);
            post.Parameters.AddWithValue("owner", owner);
            await post.ExecuteNonQueryAsync(ct);
        }

        Assert.True(await reader.HasPostedAsync(chat, owner, ct));
        Assert.False(await reader.HasPostedAsync(Guid.NewGuid(), owner, ct));
    }

    [Fact]
    public async Task The_agent_identity_carries_the_stored_role_and_an_empty_role_when_unset()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = await CreateAsync(ct);
        var withRole = Guid.NewGuid();
        var withoutRole = Guid.NewGuid();
        await using (var seed = source.CreateCommand("INSERT INTO agent (agent_id, name, role) VALUES (@a, 'Charles', 'Engineer'), (@b, 'Claude', NULL)"))
        {
            seed.Parameters.AddWithValue("a", withRole);
            seed.Parameters.AddWithValue("b", withoutRole);
            await seed.ExecuteNonQueryAsync(ct);
        }

        var reader = new AgentRepositoryIdentityReader(source);
        Assert.Equal(new Bazaar.Server.SoulProfile.AgentIdentity("Charles", "Engineer", ""), await reader.ReadAsync(withRole, ct));
        Assert.Equal(new Bazaar.Server.SoulProfile.AgentIdentity("Claude", "", ""), await reader.ReadAsync(withoutRole, ct));
        Assert.Null(await reader.ReadAsync(Guid.NewGuid(), ct));
    }
}
