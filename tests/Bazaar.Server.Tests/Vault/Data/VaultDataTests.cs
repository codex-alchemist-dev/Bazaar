using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Vault.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Vault.Data;

public sealed class VaultDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
        {
            return;
        }

        await base.InitializeAsync();
        var source = new EmbeddedMigrationSource(typeof(AssemblyMarker).Assembly);
        await new MigrationRunner(DataSource).MigrateAsync(source, "linux");
    }
}

/// <summary>Real-Postgres CRUD and constraints for the Vault index tables (migration common/0009).</summary>
public sealed class VaultDataTests(VaultDb db) : IClassFixture<VaultDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 26, 6, 0, 0, TimeSpan.Zero);
    private const string _commitA = "1111111111111111111111111111111111111111";
    private const string _commitB = "2222222222222222222222222222222222222222";
    private const string _commitC = "3333333333333333333333333333333333333333";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static VaultDocRecord Doc(Guid vaultId, Guid docId, string path, string type = "research", string? owner = null) =>
        new(docId, vaultId, path, path, type, null, "shared/research", owner, "{}", null, _t0);

    private static VaultBlockRecord Block(int ordinal, string body, string? blockId = null) =>
        new(ordinal, blockId, ["Heading"], ordinal * 2 + 1, ordinal * 2 + 2, Hash(body), body);

    private static VaultLinkRecord Link(Guid src, int ordinal, int linkOrd, string targetRaw, Guid? target) =>
        new(src, ordinal, linkOrd, "link", targetRaw, target, null, null);

    private async Task<Guid> NewProfileAsync(int dimensions)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO embedding_profile (provider_id, model_id, dimensions) VALUES ('test', 'fake', $1) RETURNING profile_id");
        cmd.Parameters.AddWithValue(dimensions);
        return (Guid)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter);
        }

        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Document_blocks_and_outgoing_links_round_trip()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(vault, b, "projects/x/Plan.md"), [Block(0, "plan text", "p1")], [], Ct);
        await repo.ReplaceDocumentAsync(
            Doc(vault, a, "projects/x/Index.md"),
            [Block(0, "see [[Plan]]"), Block(1, "other")],
            [Link(a, 0, 0, "Plan", b), Link(a, 0, 1, "Missing", null)],
            Ct);

        var read = await repo.GetDocumentByPathAsync(vault, "projects/x/Index.md", Ct);
        Assert.NotNull(read);
        Assert.Equal(a, read.DocId);
        Assert.Equal(_t0, read.UpdatedAt);

        var blocks = await repo.GetBlocksAsync(a, Ct);
        Assert.Equal([0, 1], blocks.Select(block => block.Ordinal));
        Assert.Equal(["Heading"], blocks[0].HeadingPath);
        Assert.Equal(Hash("other"), blocks[1].TextHash);

        var links = await repo.GetOutgoingLinksAsync(a, Ct);
        Assert.Equal(2, links.Count);
        Assert.Equal(b, links[0].TargetDoc);
        Assert.Null(links[1].TargetDoc);
        Assert.Equal("Missing", links[1].TargetRaw);
    }

    [Fact]
    public async Task Replacing_with_a_new_path_renames_and_keeps_the_id()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var doc = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(vault, doc, "old/Name.md"), [Block(0, "body")], [], Ct);
        await repo.ReplaceDocumentAsync(Doc(vault, doc, "new/Name.md"), [Block(0, "body")], [], Ct);

        Assert.Null(await repo.GetDocumentByPathAsync(vault, "old/Name.md", Ct));
        Assert.Equal(doc, (await repo.GetDocumentByPathAsync(vault, "new/Name.md", Ct))!.DocId);
    }

    [Fact]
    public async Task Embeddings_survive_only_while_their_block_text_is_unchanged()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var embeddings = new VaultEmbeddingRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var profile = await NewProfileAsync(3);
        await repo.ReplaceDocumentAsync(Doc(vault, doc, "shared/x.md"), [Block(0, "keep"), Block(1, "change")], [], Ct);
        await embeddings.UpsertAsync(doc, profile, [
            new VaultBlockEmbedding(0, Hash("keep"), [1f, 0f, 0f]),
            new VaultBlockEmbedding(1, Hash("change"), [0f, 1f, 0f]),
        ], Ct);

        await repo.ReplaceDocumentAsync(Doc(vault, doc, "shared/x.md"), [Block(0, "keep"), Block(1, "changed")], [], Ct);

        var stored = await embeddings.GetStoredHashesAsync(doc, profile, Ct);
        Assert.Equal([0], stored.Keys);
        Assert.Equal(Hash("keep"), stored[0]);
    }

    [Fact]
    public async Task Shrinking_a_document_drops_stale_blocks_and_their_embeddings()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var embeddings = new VaultEmbeddingRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var profile = await NewProfileAsync(3);
        await repo.ReplaceDocumentAsync(Doc(vault, doc, "shared/shrink.md"), [Block(0, "one"), Block(1, "two")], [], Ct);
        await embeddings.UpsertAsync(doc, profile, [new VaultBlockEmbedding(1, Hash("two"), [1f, 1f, 1f])], Ct);

        await repo.ReplaceDocumentAsync(Doc(vault, doc, "shared/shrink.md"), [Block(0, "one")], [], Ct);

        Assert.Single(await repo.GetBlocksAsync(doc, Ct));
        Assert.Empty(await embeddings.GetStoredHashesAsync(doc, profile, Ct));
    }

    [Fact]
    public async Task Embedding_with_the_wrong_dimension_is_rejected()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var embeddings = new VaultEmbeddingRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var profile = await NewProfileAsync(3);
        await repo.ReplaceDocumentAsync(Doc(vault, doc, "shared/dims.md"), [Block(0, "body")], [], Ct);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            embeddings.UpsertAsync(doc, profile, [new VaultBlockEmbedding(0, Hash("body"), [1f, 2f])], Ct));
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Non_finite_embedding_values_are_rejected_before_the_database()
    {
        SkipWithoutDatabase();
        var embeddings = new VaultEmbeddingRepository(db.DataSource);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            embeddings.UpsertAsync(Guid.NewGuid(), Guid.NewGuid(), [new VaultBlockEmbedding(0, Hash("x"), [float.NaN])], Ct));
    }

    [Fact]
    public async Task Path_is_unique_within_a_vault_but_not_across_vaults()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(vault, Guid.NewGuid(), "dup.md"), [Block(0, "one")], [], Ct);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.ReplaceDocumentAsync(Doc(vault, Guid.NewGuid(), "dup.md"), [Block(0, "two")], [], Ct));
        Assert.Equal("23505", error.SqlState);

        await repo.ReplaceDocumentAsync(Doc(Guid.NewGuid(), Guid.NewGuid(), "dup.md"), [Block(0, "three")], [], Ct);
    }

    [Fact]
    public async Task Unknown_document_type_is_rejected()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.ReplaceDocumentAsync(Doc(Guid.NewGuid(), Guid.NewGuid(), "bad.md", type: "notes"), [], [], Ct));
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Owner_is_allowed_only_on_memory_and_charter_documents()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(vault, Guid.NewGuid(), "agents/a/memory/x.md", "memory", "agent:a"), [], [], Ct);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            repo.ReplaceDocumentAsync(Doc(vault, Guid.NewGuid(), "plans/y.md", "plan", "agent:a"), [], [], Ct));
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Backlinks_and_unresolved_links_are_listed()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var target = Guid.NewGuid();
        var source = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(vault, target, "Target.md"), [Block(0, "t")], [], Ct);
        await repo.ReplaceDocumentAsync(
            Doc(vault, source, "Source.md"),
            [Block(0, "s")],
            [Link(source, 0, 0, "Target", target), Link(source, 0, 1, "Nowhere", null)],
            Ct);

        var backlinks = await repo.GetBacklinksAsync(target, Ct);
        Assert.Single(backlinks);
        Assert.Equal(source, backlinks[0].SrcDoc);

        var unresolved = await repo.GetUnresolvedLinksAsync(vault, Ct);
        Assert.Equal(["Nowhere"], unresolved.Select(link => link.TargetRaw));
    }

    [Fact]
    public async Task Deleting_a_document_leaves_inbound_links_unresolved()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var target = Guid.NewGuid();
        var source = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(vault, target, "Gone.md"), [Block(0, "g")], [], Ct);
        await repo.ReplaceDocumentAsync(Doc(vault, source, "Keeps.md"), [Block(0, "k")], [Link(source, 0, 0, "Gone", target)], Ct);

        Assert.True(await repo.DeleteDocumentAsync(target, Ct));

        Assert.Null(await repo.GetDocumentAsync(target, Ct));
        Assert.Empty(await repo.GetBlocksAsync(target, Ct));
        var link = Assert.Single(await repo.GetOutgoingLinksAsync(source, Ct));
        Assert.Null(link.TargetDoc);
        Assert.Equal("Gone", link.TargetRaw);
        Assert.False(await repo.DeleteDocumentAsync(target, Ct));
    }

    [Fact]
    public async Task Deleting_a_document_removes_its_embeddings()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var embeddings = new VaultEmbeddingRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var profile = await NewProfileAsync(3);
        await repo.ReplaceDocumentAsync(Doc(vault, doc, "shared/embedded.md"), [Block(0, "e")], [], Ct);
        await embeddings.UpsertAsync(doc, profile, [new VaultBlockEmbedding(0, Hash("e"), [0f, 0f, 1f])], Ct);

        Assert.True(await repo.DeleteDocumentAsync(doc, Ct));

        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM vault_block_embedding WHERE doc_id = $1", doc));
    }

    [Fact]
    public async Task Full_text_index_matches_block_body()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var doc = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(vault, doc, "shared/fts.md"), [Block(0, "Token flow for authentication")], [], Ct);

        var hits = await ScalarAsync<long>(
            "SELECT count(*) FROM vault_blocks WHERE doc_id = $1 AND tsv @@ plainto_tsquery('english', 'authentication')", doc);
        Assert.Equal(1L, hits);
    }

    [Fact]
    public async Task Commits_insert_once_and_derive_is_merge_from_parents()
    {
        SkipWithoutDatabase();
        var commits = new VaultCommitRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var root = new VaultCommitRecord(vault, _commitA, [], "agent-a", _t0, ["refs/heads/main"], ["Plan.md"], null, null);
        var merge = new VaultCommitRecord(vault, _commitC, [_commitA, _commitB], "agent-b", _t0.AddMinutes(5), [], ["Plan.md"],
            Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(2, await commits.InsertAsync([root, merge], Ct));
        Assert.Equal(0, await commits.InsertAsync([root], Ct));

        var stored = await commits.GetAsync(vault, _commitC, Ct);
        Assert.NotNull(stored);
        Assert.Equal([_commitA, _commitB], stored.Parents);
        Assert.NotNull(stored.GcId);
        Assert.Equal(_commitC, (await commits.ListRecentAsync(vault, 1, Ct)).Single().Sha);
        Assert.True(await ScalarAsync<bool>("SELECT is_merge FROM vault_commit WHERE vault_id = $1 AND sha = $2", vault, _commitC));
        Assert.False(await ScalarAsync<bool>("SELECT is_merge FROM vault_commit WHERE vault_id = $1 AND sha = $2", vault, _commitA));
    }
    [Fact]
    public async Task Commit_with_a_wrong_merge_flag_is_rejected()
    {
        SkipWithoutDatabase();
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = db.DataSource.CreateCommand("""
                INSERT INTO vault_commit (vault_id, sha, parents, author, committed_at, is_merge)
                VALUES ($1, $2, ARRAY[$3, $4]::char(40)[], 'x', now(), false)
                """);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(_commitC);
            cmd.Parameters.AddWithValue(_commitA);
            cmd.Parameters.AddWithValue(_commitB);
            await cmd.ExecuteNonQueryAsync(Ct);
        });
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Indexed_commit_advances_only_from_the_stored_value()
    {
        SkipWithoutDatabase();
        var state = new VaultStateRepository(db.DataSource);
        var vault = Guid.NewGuid();

        Assert.Null(await state.GetIndexedCommitAsync(vault, Ct));
        Assert.True(await state.TryAdvanceAsync(vault, null, _commitA, Ct));
        Assert.False(await state.TryAdvanceAsync(vault, null, _commitB, Ct));
        Assert.False(await state.TryAdvanceAsync(vault, _commitB, _commitC, Ct));
        Assert.True(await state.TryAdvanceAsync(vault, _commitA, _commitB, Ct));
        Assert.Equal(_commitB, await state.GetIndexedCommitAsync(vault, Ct));
    }

    [Fact]
    public async Task Deleting_one_profile_leaves_the_other_profiles_embeddings()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var embeddings = new VaultEmbeddingRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var kept = await NewProfileAsync(2);
        var dropped = await NewProfileAsync(2);
        await repo.ReplaceDocumentAsync(Doc(vault, doc, "shared/profiles.md"), [Block(0, "p")], [], Ct);
        await embeddings.UpsertAsync(doc, kept, [new VaultBlockEmbedding(0, Hash("p"), [1f, 0f])], Ct);
        await embeddings.UpsertAsync(doc, dropped, [new VaultBlockEmbedding(0, Hash("p"), [0f, 1f])], Ct);

        Assert.Equal(1, await embeddings.DeleteForProfileAsync(dropped, Ct));

        Assert.Single(await embeddings.GetStoredHashesAsync(doc, kept, Ct));
        Assert.Empty(await embeddings.GetStoredHashesAsync(doc, dropped, Ct));
    }
}
