using Npgsql;

namespace Bazaar.Server.Embeddings.Tests.Postgres;

/// <summary>Real-Postgres tests for <see cref="PostgresEmbeddingProfileStore"/> (doc 04, <c>embedding_profile</c>).</summary>
public sealed class PostgresEmbeddingProfileStoreTests(EmbeddingsDb db) : IClassFixture<EmbeddingsDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each test uses its own provider id so rows from other tests in the shared class database never interfere.
    private static string NewProvider() => "p-" + Guid.NewGuid().ToString("N");

    private async Task<int> ActiveCountAsync()
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM embedding_profile WHERE active");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Create_returns_an_inactive_row_with_the_given_dimension()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var provider = NewProvider();

        var created = await store.CreateAsync(provider, "m1", 384, Ct);

        Assert.NotEqual(Guid.Empty, created.ProfileId);
        Assert.Equal(provider, created.ProviderId);
        Assert.Equal("m1", created.ModelId);
        Assert.Equal(384, created.Dimensions);
        Assert.False(created.Active);
    }

    [Fact]
    public async Task Create_rejects_a_non_positive_dimension_before_touching_the_database()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CreateAsync(NewProvider(), "m", 0, Ct));
    }

    [Fact]
    public async Task Find_returns_null_when_absent_and_the_row_when_present()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var provider = NewProvider();

        Assert.Null(await store.FindAsync(provider, "m", 8, Ct));
        var created = await store.CreateAsync(provider, "m", 8, Ct);

        var found = await store.FindAsync(provider, "m", 8, Ct);
        Assert.NotNull(found);
        Assert.Equal(created.ProfileId, found.ProfileId);
    }

    [Fact]
    public async Task Find_matches_provider_model_and_dimension_exactly_so_a_repeated_setup_reuses_its_row()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var provider = NewProvider();
        var a = await store.CreateAsync(provider, "m", 8, Ct);
        var b = await store.CreateAsync(provider, "m", 16, Ct);
        var c = await store.CreateAsync(provider, "other", 8, Ct);

        Assert.Equal(a.ProfileId, (await store.FindAsync(provider, "m", 8, Ct))!.ProfileId);
        Assert.Equal(b.ProfileId, (await store.FindAsync(provider, "m", 16, Ct))!.ProfileId);
        Assert.Equal(c.ProfileId, (await store.FindAsync(provider, "other", 8, Ct))!.ProfileId);
        Assert.Null(await store.FindAsync(provider, "m", 32, Ct));
        Assert.Null(await store.FindAsync(provider + "x", "m", 8, Ct));
    }

    [Fact]
    public async Task Find_prefers_the_active_row_when_duplicates_exist()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var provider = NewProvider();
        await store.CreateAsync(provider, "m", 8, Ct);
        var second = await store.CreateAsync(provider, "m", 8, Ct);
        await store.ActivateAsync(second.ProfileId, Ct);

        var found = await store.FindAsync(provider, "m", 8, Ct);

        Assert.Equal(second.ProfileId, found!.ProfileId);
        Assert.True(found.Active);
    }

    [Fact]
    public async Task Activate_switches_the_single_active_profile_atomically()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var provider = NewProvider();
        var first = await store.CreateAsync(provider, "m", 8, Ct);
        var second = await store.CreateAsync(provider, "m2", 16, Ct);

        await store.ActivateAsync(first.ProfileId, Ct);
        Assert.Equal(first.ProfileId, (await store.GetActiveAsync(Ct))!.ProfileId);

        await store.ActivateAsync(second.ProfileId, Ct);
        var active = await store.GetActiveAsync(Ct);
        Assert.Equal(second.ProfileId, active!.ProfileId);
        Assert.Equal(1, await ActiveCountAsync());
        Assert.False((await store.FindAsync(provider, "m", 8, Ct))!.Active);

        // Activating the already-active profile is a no-op, not an error and not a gap with zero active rows.
        await store.ActivateAsync(second.ProfileId, Ct);
        Assert.Equal(1, await ActiveCountAsync());
        Assert.Equal(second.ProfileId, (await store.GetActiveAsync(Ct))!.ProfileId);
    }

    [Fact]
    public async Task Activate_of_an_unknown_profile_throws_and_leaves_the_current_active_profile_in_place()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var keep = await store.CreateAsync(NewProvider(), "m", 8, Ct);
        await store.ActivateAsync(keep.ProfileId, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ActivateAsync(Guid.NewGuid(), Ct));

        // The failed switch rolled back with its transaction: the old profile is still the one active row.
        Assert.Equal(1, await ActiveCountAsync());
        Assert.Equal(keep.ProfileId, (await store.GetActiveAsync(Ct))!.ProfileId);
    }

    [Fact]
    public async Task Concurrent_activations_always_leave_exactly_one_active_profile()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var provider = NewProvider();
        var rows = new List<EmbeddingProfile>();
        for (var i = 0; i < 4; i++)
        {
            rows.Add(await store.CreateAsync(provider, "m" + i, 8 + i, Ct));
        }

        // Concurrent switches may lose a race and hit the one-active unique index; that is a clean failure, never two active rows.
        var results = await Task.WhenAll(rows.Select(async r =>
        {
            try
            {
                await store.ActivateAsync(r.ProfileId, Ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
            }
            return 0;
        }));

        Assert.Equal(rows.Count, results.Length);
        Assert.Equal(1, await ActiveCountAsync());
    }

    [Fact]
    public async Task GetActive_returns_null_when_no_profile_is_active()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        await using (var cmd = db.DataSource.CreateCommand("UPDATE embedding_profile SET active = false"))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        Assert.Null(await new PostgresEmbeddingProfileStore(db.DataSource).GetActiveAsync(Ct));
    }

    [Fact]
    public async Task Dimension_is_fixed_once_a_profile_holds_vectors_of_that_size()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var profile = await store.CreateAsync(NewProvider(), "m", 3, Ct);
        var (docId, ordinal, hash) = await EmbeddingsSeed.InsertBlockAsync(db.DataSource, "text", Ct);

        // The trigger on vault_block_embedding checks every vector against the profile's dimension.
        var index = new Embeddings.Indexing.PostgresEmbeddingIndexStore(db.DataSource);
        var bad = await Assert.ThrowsAnyAsync<PostgresException>(() => index.UpsertAsync(
            profile.ProfileId, [new Embeddings.Indexing.EmbeddedBlock(docId, ordinal, hash, [1f, 2f])], Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bad.SqlState);

        Assert.Equal(1, await index.UpsertAsync(
            profile.ProfileId, [new Embeddings.Indexing.EmbeddedBlock(docId, ordinal, hash, [1f, 2f, 3f])], Ct));

        // A different dimension is a different profile row, never an edit of this one.
        var wider = await store.CreateAsync(profile.ProviderId, profile.ModelId, 4, Ct);
        Assert.NotEqual(profile.ProfileId, wider.ProfileId);
        Assert.Equal(3, (await store.FindAsync(profile.ProviderId, profile.ModelId, 3, Ct))!.Dimensions);
    }

    [Fact]
    public async Task Created_profiles_are_all_listed_with_their_flags()
    {
        EmbeddingsDb.SkipWithoutDatabase();
        var store = new PostgresEmbeddingProfileStore(db.DataSource);
        var provider = NewProvider();
        var a = await store.CreateAsync(provider, "a", 8, Ct);
        var b = await store.CreateAsync(provider, "b", 8, Ct);
        await store.ActivateAsync(b.ProfileId, Ct);

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT profile_id, active FROM embedding_profile WHERE provider_id = $1 ORDER BY created_at, profile_id");
        cmd.Parameters.AddWithValue(provider);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var listed = new List<(Guid Id, bool Active)>();
        while (await reader.ReadAsync(Ct))
        {
            listed.Add((reader.GetGuid(0), reader.GetBoolean(1)));
        }

        Assert.Equal([(a.ProfileId, false), (b.ProfileId, true)], listed);
    }
}
