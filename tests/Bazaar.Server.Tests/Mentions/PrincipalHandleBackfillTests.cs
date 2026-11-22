using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Mentions;
using Bazaar.Server.Tests.Gc;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Mentions;

/// <summary>
/// A cloned test database migrated with every namespace except <c>mentions</c>, so a test can insert agents first and then
/// apply <c>mentions/0001</c> to see its backfill.
/// </summary>
public sealed class MentionsBackfillDb : PgFixture, IAsyncLifetime
{
    /// <summary>The namespace this fixture holds back.</summary>
    public const string Namespace = "mentions";

    private static EmbeddedMigrationSource Embedded => new(typeof(MigrationRunner).Assembly);

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new WithoutNamespace(Embedded, Namespace), "linux", CancellationToken.None);
    }

    /// <summary>Applies what is still pending, which is the <c>mentions</c> namespace. Returns what was applied.</summary>
    public async Task<MigrationResult> ApplyMentionsAsync(CancellationToken ct) =>
        await new MigrationRunner(DataSource, new MigrationOptions(16)).MigrateAsync(Embedded, "linux", ct);

    private sealed class WithoutNamespace(IMigrationSource inner, string excluded) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => [.. inner.Namespaces().Where(ns => ns != excluded)];

        public IReadOnlyList<MigrationFile> Load(string ns) => ns == excluded ? [] : inner.Load(ns);
    }
}

/// <summary>The backfill in <c>mentions/0001_principal_handle.sql</c>: one handle per existing agent, from its name.</summary>
public sealed class PrincipalHandleBackfillTests(MentionsBackfillDb db) : IClassFixture<MentionsBackfillDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> NewAgentAsync(string name, int minute, string state = "active")
    {
        var id = Guid.CreateVersion7();
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO agent (agent_id, name, state, created_at) VALUES ($1, $2, $3, timestamptz '2026-01-01 00:00:00+00' + make_interval(mins => $4))");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = state });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = minute });
        await cmd.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private async Task<string?> HandleOfAsync(Guid agent)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT handle FROM principal_handle WHERE principal_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agent });
        return await cmd.ExecuteScalarAsync(Ct) as string;
    }

    // One test: the migration runs once per database, so every case is set up before it and checked after it.
    [Fact]
    public async Task The_migration_gives_each_existing_agent_a_handle_from_its_name()
    {
        GcTestSkip.WithoutDatabase();

        // Plain names. Each must come out exactly as HandleSlug.FromName gives it.
        string[] plainNames =
        [
            "Charles",
            "Grace Hopper",
            "Dr. Ada O'Neil",
            "Build-Bot 9000",
            "build_bot",
            "  padded  name  ",
            "__under__",
            "R2D2",
            "Zoë Quinn",
            "日本 taro",
            new string('q', 50),
            new string('w', 31) + " tail",
        ];
        var plain = new List<(Guid Id, string Name)>();
        var minute = 0;
        foreach (var name in plainNames)
            plain.Add((await NewAgentAsync(name, minute++), name));

        // Collisions: the oldest agent keeps the plain slug, later ones get -2, -3. Case and punctuation do not tell them apart.
        var twinFirst = await NewAgentAsync("Ada Lovelace", 100);
        var twinSecond = await NewAgentAsync("ada lovelace", 101);
        var twinThird = await NewAgentAsync("Ada  (Lovelace)", 102);

        // A collision on a full-length slug cuts the slug so the suffix fits.
        var longName = new string('z', 40);
        var longFirst = await NewAgentAsync(longName, 110);
        var longSecond = await NewAgentAsync(longName, 111);

        // A name whose suffixed form is already another agent's own slug: the later twin moves on to -3.
        var suffixOwner = await NewAgentAsync("Pair-2", 120);
        var pairFirst = await NewAgentAsync("Pair", 121);
        var pairSecond = await NewAgentAsync("Pair", 122);

        // Broadcast words are never handles.
        var all = await NewAgentAsync("All", 130);
        var here = await NewAgentAsync("here", 131);

        // Nothing usable: no row.
        var symbols = await NewAgentAsync("!!!", 140);
        var oneChar = await NewAgentAsync("x", 141);
        var nonAscii = await NewAgentAsync("日本語", 142);
        var separators = await NewAgentAsync("-_-", 143);

        // Every agent is covered, whatever its state.
        var retired = await NewAgentAsync("Retired Rita", 150, "retired");

        var result = await db.ApplyMentionsAsync(Ct);

        Assert.Contains(result.Applied, m => m.Namespace == MentionsBackfillDb.Namespace && m.Version == 1);
        Assert.All(result.Applied, m => Assert.Equal(MentionsBackfillDb.Namespace, m.Namespace));

        foreach (var (id, name) in plain)
        {
            var expected = HandleSlug.FromName(name);
            Assert.NotNull(expected);
            Assert.Equal(expected, await HandleOfAsync(id));
        }

        Assert.Equal("ada-lovelace", await HandleOfAsync(twinFirst));
        Assert.Equal("ada-lovelace-2", await HandleOfAsync(twinSecond));
        Assert.Equal("ada-lovelace-3", await HandleOfAsync(twinThird));

        Assert.Equal(new string('z', 32), await HandleOfAsync(longFirst));
        Assert.Equal(HandleSlug.WithSuffix(new string('z', 32), 2), await HandleOfAsync(longSecond));
        Assert.Equal(new string('z', 30) + "-2", await HandleOfAsync(longSecond));

        Assert.Equal("pair-2", await HandleOfAsync(suffixOwner));
        Assert.Equal("pair", await HandleOfAsync(pairFirst));
        Assert.Equal("pair-3", await HandleOfAsync(pairSecond));

        Assert.Equal("all-2", await HandleOfAsync(all));
        Assert.Equal("here-2", await HandleOfAsync(here));

        Assert.Null(await HandleOfAsync(symbols));
        Assert.Null(await HandleOfAsync(oneChar));
        Assert.Null(await HandleOfAsync(nonAscii));
        Assert.Null(await HandleOfAsync(separators));

        Assert.Equal("retired-rita", await HandleOfAsync(retired));

        // Every backfilled row is an agent row with a valid, distinct handle.
        var handles = new List<string>();
        await using (var cmd = db.DataSource.CreateCommand("SELECT principal_kind, handle FROM principal_handle"))
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                Assert.Equal("agent", reader.GetString(0));
                handles.Add(reader.GetString(1));
            }
        }

        Assert.All(handles, h => Assert.True(HandleSlug.IsValid(h), h));
        Assert.Equal(handles.Count, handles.Distinct(StringComparer.Ordinal).Count());
        // 12 plain + 3 twins + 2 long + 3 pair + 2 broadcast + 1 retired; the 4 unusable names have no row.
        Assert.Equal(23, handles.Count);
    }
}
