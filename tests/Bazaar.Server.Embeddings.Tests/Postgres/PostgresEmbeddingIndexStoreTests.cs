using Bazaar.Server.Embeddings.Indexing;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Embeddings.Tests.Postgres;

/// <summary>Real-Postgres tests for <see cref="PostgresEmbeddingIndexStore"/> over <c>vault_blocks</c> and <c>vault_block_embedding</c>.</summary>
public sealed class PostgresEmbeddingIndexStoreTests(EmbeddingsDb db) : IClassFixture<EmbeddingsDb>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PostgresEmbeddingIndexStore Store => new(db.DataSource);

    public async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
        {
            return;
        }

        // Tests in a class run one after another on the shared database, so each starts from empty Vault and embedding tables.
        foreach (var table in new[] { "vault_block_embedding", "vault_blocks", "vault_docs" })
        {
            await using var cmd = db.DataSource.CreateCommand($"DELETE FROM {table}");
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Doc ids are fixed and ascending so the (doc_id, ordinal) order is known.
    private static Guid DocId(int n) => new($"{n:x8}-0000-0000-0000-000000000000");

    private async Task<Guid> NewProfileAsync(int dimensions)
    {
        var profile = await new PostgresEmbeddingProfileStore(db.DataSource)
            .CreateAsync("p-" + Guid.NewGuid().ToString("N"), "m", dimensions, Ct);
        return profile.ProfileId;
    }

    private async Task SeedAsync(int docs, int blocksPerDoc)
    {
        for (var d = 1; d <= docs; d++)
        {
            await EmbeddingsSeed.InsertDocAsync(db.DataSource, DocId(d), Ct);
            for (var o = 0; o < blocksPerDoc; o++)
            {
                await EmbeddingsSeed.InsertBlockAsync(db.DataSource, DocId(d), o, $"doc {d} block {o}", Ct);
            }
        }
    }

    private static EmbeddedBlock Embedded(PendingBlock block, params float[] vector) => new(block.DocId, block.Ordinal, block.TextHash, vector);

    [Fact]
    public async Task Pending_lists_every_block_in_doc_then_ordinal_order()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(2);
        await SeedAsync(docs: 3, blocksPerDoc: 2);

        var page = await Store.ListPendingAsync(profile, null, 100, Ct);

        Assert.Equal(
            [(1, 0), (1, 1), (2, 0), (2, 1), (3, 0), (3, 1)],
            page.Select(p => (Array.IndexOf([DocId(1), DocId(2), DocId(3)], p.DocId) + 1, p.Ordinal)));
        Assert.Equal("doc 2 block 1", page[3].Body);
        Assert.Equal(EmbeddingsSeed.Hash("doc 2 block 1"), page[3].TextHash);
    }

    [Fact]
    public async Task Keyset_paging_walks_all_blocks_once_with_the_doc_ordinal_cursor()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(2);
        await SeedAsync(docs: 4, blocksPerDoc: 3);

        var seen = new List<(Guid, int)>();
        BlockCursor? cursor = null;
        while (true)
        {
            var page = await Store.ListPendingAsync(profile, cursor, 5, Ct);
            if (page.Count == 0)
            {
                break;
            }

            Assert.True(page.Count <= 5);
            seen.AddRange(page.Select(p => (p.DocId, p.Ordinal)));
            cursor = new BlockCursor(page[^1].DocId, page[^1].Ordinal);
        }

        Assert.Equal(12, seen.Count);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.Equal(seen.Order().ToList(), seen);
    }

    [Fact]
    public async Task Cursor_is_exclusive_and_compares_the_pair_not_each_column()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(2);
        await SeedAsync(docs: 2, blocksPerDoc: 3);

        // After (doc 1, ordinal 1): the rest of doc 1 and all of doc 2, including ordinals below the cursor's ordinal.
        var page = await Store.ListPendingAsync(profile, new BlockCursor(DocId(1), 1), 100, Ct);

        Assert.Equal([(DocId(1), 2), (DocId(2), 0), (DocId(2), 1), (DocId(2), 2)], page.Select(p => (p.DocId, p.Ordinal)));
    }

    [Fact]
    public async Task Null_cursor_does_not_filter_even_though_the_ordinal_parameter_is_sent_as_zero()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(2);
        await SeedAsync(docs: 1, blocksPerDoc: 2);

        var first = await Store.ListPendingAsync(profile, null, 100, Ct);

        // Ordinal 0 of the first doc is included: the null guard short-circuits the comparison.
        Assert.Equal([0, 1], first.Select(p => p.Ordinal));
        Assert.Empty(await Store.ListPendingAsync(profile, new BlockCursor(DocId(1), 1), 100, Ct));
    }

    [Fact]
    public async Task Limit_must_be_positive_and_is_applied()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(2);
        await SeedAsync(docs: 1, blocksPerDoc: 4);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store.ListPendingAsync(profile, null, 0, Ct));
        Assert.Equal(2, (await Store.ListPendingAsync(profile, null, 2, Ct)).Count);
    }

    [Fact]
    public async Task Upsert_stores_a_halfvec_and_the_block_stops_being_pending()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(3);
        await SeedAsync(docs: 1, blocksPerDoc: 2);
        var pending = await Store.ListPendingAsync(profile, null, 10, Ct);

        var written = await Store.UpsertAsync(profile, [Embedded(pending[0], 0.5f, -1f, 2f)], Ct);

        Assert.Equal(1, written);
        var remaining = await Store.ListPendingAsync(profile, null, 10, Ct);
        Assert.Equal([1], remaining.Select(p => p.Ordinal));

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT embedding::text, vector_dims(embedding), pg_typeof(embedding)::text FROM vault_block_embedding WHERE profile_id = $1");
        cmd.Parameters.AddWithValue(profile);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal("[0.5,-1,2]", reader.GetString(0));
        Assert.Equal(3, reader.GetInt32(1));
        Assert.Equal("halfvec", reader.GetString(2));
    }

    [Fact]
    public async Task Upsert_of_an_empty_batch_writes_nothing()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(3);

        Assert.Equal(0, await Store.UpsertAsync(profile, [], Ct));
    }

    [Fact]
    public async Task Upsert_replaces_the_row_for_the_same_block_and_profile()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(2);
        await SeedAsync(docs: 1, blocksPerDoc: 1);
        var block = (await Store.ListPendingAsync(profile, null, 10, Ct)).Single();

        await Store.UpsertAsync(profile, [Embedded(block, 1f, 1f)], Ct);
        await Store.UpsertAsync(profile, [Embedded(block, 3f, 4f)], Ct);

        await using var cmd = db.DataSource.CreateCommand("SELECT count(*), min(embedding::text) FROM vault_block_embedding");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("[3,4]", reader.GetString(1));
    }

    [Fact]
    public async Task A_changed_block_text_makes_the_stored_embedding_stale_and_pending_again()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(2);
        await SeedAsync(docs: 1, blocksPerDoc: 2);
        var pending = await Store.ListPendingAsync(profile, null, 10, Ct);
        await Store.UpsertAsync(profile, [.. pending.Select(p => Embedded(p, 1f, 2f))], Ct);
        Assert.Empty(await Store.ListPendingAsync(profile, null, 10, Ct));

        await EmbeddingsSeed.UpdateBlockBodyAsync(db.DataSource, DocId(1), 1, "edited text", Ct);

        var stale = await Store.ListPendingAsync(profile, null, 10, Ct);
        var block = Assert.Single(stale);
        Assert.Equal(1, block.Ordinal);
        Assert.Equal("edited text", block.Body);

        // Re-embedding under the new hash clears it again.
        await Store.UpsertAsync(profile, [Embedded(block, 5f, 6f)], Ct);
        Assert.Empty(await Store.ListPendingAsync(profile, null, 10, Ct));
    }

    [Fact]
    public async Task Upsert_with_the_wrong_dimension_is_rejected_by_the_profile_trigger_and_writes_nothing()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profile = await NewProfileAsync(3);
        await SeedAsync(docs: 1, blocksPerDoc: 2);
        var pending = await Store.ListPendingAsync(profile, null, 10, Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            Store.UpsertAsync(profile, [Embedded(pending[0], 1f, 2f, 3f), Embedded(pending[1], 1f, 2f)], Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(2, (await Store.ListPendingAsync(profile, null, 10, Ct)).Count);
    }

    [Fact]
    public async Task Profiles_never_mix_so_each_profile_has_its_own_pending_set()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var small = await NewProfileAsync(2);
        var wide = await NewProfileAsync(4);
        await SeedAsync(docs: 1, blocksPerDoc: 2);
        var pending = await Store.ListPendingAsync(small, null, 10, Ct);

        await Store.UpsertAsync(small, [.. pending.Select(p => Embedded(p, 1f, 2f))], Ct);

        Assert.Empty(await Store.ListPendingAsync(small, null, 10, Ct));
        Assert.Equal(2, (await Store.ListPendingAsync(wide, null, 10, Ct)).Count);

        await Store.UpsertAsync(wide, [.. pending.Select(p => Embedded(p, 1f, 2f, 3f, 4f))], Ct);
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT profile_id, count(*), min(vector_dims(embedding)) FROM vault_block_embedding GROUP BY profile_id");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var dims = new Dictionary<Guid, (long Count, int Dims)>();
        while (await reader.ReadAsync(Ct))
        {
            dims[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetInt32(2));
        }

        Assert.Equal((2, 2), dims[small]);
        Assert.Equal((2, 4), dims[wide]);
    }

    [Fact]
    public async Task The_indexer_over_the_real_store_never_embeds_personal_data_blocks_and_still_walks_past_them()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var profileRow = await new PostgresEmbeddingProfileStore(db.DataSource).CreateAsync("fake", "fake-hash", 8, Ct);
        var profileId = profileRow.ProfileId;
        await EmbeddingsSeed.InsertDocAsync(db.DataSource, DocId(1), Ct);
        await EmbeddingsSeed.InsertBlockAsync(db.DataSource, DocId(1), 0, "plain note", Ct);
        await EmbeddingsSeed.InsertBlockAsync(db.DataSource, DocId(1), 1, "```bazaar-encrypted\nsecret\n```", Ct);
        await EmbeddingsSeed.InsertBlockAsync(db.DataSource, DocId(1), 2, "another plain note", Ct);

        var selector = new FixedSelector(new ActiveEmbedding(profileRow with { Active = true }, new FakeEmbeddingProvider(8)));
        var indexer = new EmbeddingIndexer(selector, Store, new FencedPersonalDataBlockDetector(), batchSize: 2);

        var result = await indexer.RunOnceAsync(Ct);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Embedded);
        Assert.Equal(1, result.SkippedPersonal);
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT ordinal FROM vault_block_embedding WHERE profile_id = $1 ORDER BY ordinal");
        cmd.Parameters.AddWithValue(profileId);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var ordinals = new List<int>();
        while (await reader.ReadAsync(Ct))
        {
            ordinals.Add(reader.GetInt32(0));
        }

        Assert.Equal([0, 2], ordinals);
    }

    [Fact]
    public async Task Vector_literal_round_trips_through_halfvec_text()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        await using var cmd = db.DataSource.CreateCommand("SELECT $1::text::halfvec::text");
        cmd.Parameters.AddWithValue(PostgresEmbeddingIndexStore.ToVectorLiteral([0.25f, -3f, 0f]));

        Assert.Equal("[0.25,-3,0]", (string?)await cmd.ExecuteScalarAsync(Ct));
    }

    private sealed class FixedSelector(ActiveEmbedding active) : IActiveEmbeddingSelector
    {
        public Task<ActiveEmbedding?> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult<ActiveEmbedding?>(active);
    }
}
