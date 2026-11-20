using Bazaar.Server.Embeddings.Indexing;
using Bazaar.Server.Settings;
using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Embeddings.Tests;

public class SelectorTests
{
    private static EmbeddingProfile Profile(string providerId = "fake", string model = FakeEmbeddingProvider.DefaultModelId, int dims = 8) =>
        new(Guid.NewGuid(), providerId, model, dims, DateTimeOffset.UnixEpoch, true);

    [Fact]
    public async Task Setting_none_means_lexical_only_even_with_an_active_profile()
    {
        var selector = new ActiveEmbeddingSelector(
            new FakeSettingSource("none"), new FakeProfileStore(Profile()), new FakeRegistry(("fake", new FakeEmbeddingProvider())));

        Assert.Null(await selector.ResolveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task No_active_profile_means_lexical_only()
    {
        var selector = new ActiveEmbeddingSelector(
            new FakeSettingSource("fake"), new FakeProfileStore(null), new FakeRegistry(("fake", new FakeEmbeddingProvider())));

        Assert.Null(await selector.ResolveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Matching_provider_and_profile_resolve()
    {
        var profile = Profile();
        var selector = new ActiveEmbeddingSelector(
            new FakeSettingSource("fake"), new FakeProfileStore(profile), new FakeRegistry(("fake", new FakeEmbeddingProvider())));

        var active = await selector.ResolveAsync(CancellationToken.None);

        Assert.Equal(profile, active!.Profile);
    }

    [Fact]
    public async Task A_provider_with_another_dimension_is_refused()
    {
        var selector = new ActiveEmbeddingSelector(
            new FakeSettingSource("fake"), new FakeProfileStore(Profile(dims: 8)), new FakeRegistry(("fake", new FakeEmbeddingProvider(16))));

        await Assert.ThrowsAsync<EmbeddingProfileMismatchException>(() => selector.ResolveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_provider_with_another_model_is_refused()
    {
        var selector = new ActiveEmbeddingSelector(
            new FakeSettingSource("fake"), new FakeProfileStore(Profile(model: "other")), new FakeRegistry(("fake", new FakeEmbeddingProvider())));

        await Assert.ThrowsAsync<EmbeddingProfileMismatchException>(() => selector.ResolveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_setting_that_names_a_different_provider_than_the_profile_is_refused()
    {
        var selector = new ActiveEmbeddingSelector(
            new FakeSettingSource("other"), new FakeProfileStore(Profile("fake")),
            new FakeRegistry(("fake", new FakeEmbeddingProvider()), ("other", new FakeEmbeddingProvider())));

        await Assert.ThrowsAsync<EmbeddingProfileMismatchException>(() => selector.ResolveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unconfigured_provider_is_refused()
    {
        var selector = new ActiveEmbeddingSelector(new FakeSettingSource("fake"), new FakeProfileStore(Profile()), new FakeRegistry());

        await Assert.ThrowsAsync<EmbeddingProfileMismatchException>(() => selector.ResolveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Query_embedder_is_bound_to_the_profile_and_refuses_a_mismatched_provider()
    {
        var profile = Profile();
        var embedder = new VaultQueryEmbedder(profile, new FakeEmbeddingProvider());
        Assert.IsAssignableFrom<IVaultQueryEmbedder>(embedder);

        var vector = await embedder.EmbedAsync("query", CancellationToken.None);

        Assert.Equal(profile.ProfileId, embedder.ProfileId);
        Assert.Equal(8, vector.Length);
        Assert.Throws<EmbeddingProfileMismatchException>(() => new VaultQueryEmbedder(profile, new FakeEmbeddingProvider(4)));
    }
}

public class SettingTests
{
    [Fact]
    public void Default_is_none_and_install_scoped()
    {
        var key = EmbeddingSettings.EmbeddingProfileKey;

        Assert.Equal("memory.embedding_profile", key.Name);
        Assert.Equal("none", key.Default);
        Assert.Equal(SettingScope.Install, key.Scope);
        Assert.True(EmbeddingSettings.IsNone(key.Default));
        Assert.Same(key, Assert.Single(new EmbeddingSettingContribution().Settings));
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("")]
    [InlineData("-x")]
    public void Invalid_values_are_rejected(string value)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(value));

        Assert.NotNull(EmbeddingSettings.EmbeddingProfileKey.CheckValue(doc.RootElement));
    }

    [Fact]
    public void A_provider_id_is_accepted()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("\"ollama-local\"");

        Assert.Null(EmbeddingSettings.EmbeddingProfileKey.CheckValue(doc.RootElement));
    }
}

public class IndexerTests
{
    private static readonly Guid _doc1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid _doc2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static byte[] Hash(byte b) => Enumerable.Repeat(b, 32).ToArray();

    private static (EmbeddingIndexer Indexer, InMemoryIndexStore Store, CountingProvider Provider) Create(string setting = "fake", int batch = 2)
    {
        var profile = new EmbeddingProfile(Guid.NewGuid(), "fake", FakeEmbeddingProvider.DefaultModelId, 8, DateTimeOffset.UnixEpoch, true);
        var provider = new CountingProvider(new FakeEmbeddingProvider());
        var selector = new ActiveEmbeddingSelector(new FakeSettingSource(setting), new FakeProfileStore(profile), new FakeRegistry(("fake", provider)));
        var store = new InMemoryIndexStore();
        return (new EmbeddingIndexer(selector, store, new FencedPersonalDataBlockDetector(), batch), store, provider);
    }

    [Fact]
    public async Task Setting_none_keeps_the_indexer_idle_and_touches_nothing()
    {
        var (indexer, store, provider) = Create("none");
        store.Blocks.Add(new PendingBlock(_doc1, 0, "text", Hash(1)));

        var result = await indexer.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Idle);
        Assert.Equal(0, store.ListCalls);
        Assert.Empty(provider.Seen);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Embeds_missing_blocks_across_batches()
    {
        var (indexer, store, provider) = Create(batch: 2);
        for (var i = 0; i < 5; i++)
        {
            store.Blocks.Add(new PendingBlock(i < 3 ? _doc1 : _doc2, i, $"block {i}", Hash((byte)i)));
        }

        var result = await indexer.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Idle);
        Assert.Equal(5, result.Embedded);
        Assert.Equal(5, store.Stored.Count);
        Assert.Equal(5, provider.Seen.Count);
    }

    [Fact]
    public async Task A_second_run_embeds_nothing_when_hashes_are_current()
    {
        var (indexer, store, provider) = Create();
        store.Blocks.Add(new PendingBlock(_doc1, 0, "stable", Hash(1)));
        await indexer.RunOnceAsync(CancellationToken.None);
        provider.Seen.Clear();

        var again = await indexer.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, again.Embedded);
        Assert.Empty(provider.Seen);
    }

    [Fact]
    public async Task Only_stale_blocks_are_re_embedded_when_text_changes()
    {
        var (indexer, store, provider) = Create();
        store.Blocks.Add(new PendingBlock(_doc1, 0, "keep", Hash(1)));
        store.Blocks.Add(new PendingBlock(_doc1, 1, "old text", Hash(2)));
        await indexer.RunOnceAsync(CancellationToken.None);
        provider.Seen.Clear();
        store.Blocks[1] = new PendingBlock(_doc1, 1, "new text", Hash(3));

        var result = await indexer.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Embedded);
        Assert.Equal(["new text"], provider.Seen);
        Assert.True(store.Stored[(_doc1, 1)].AsSpan().SequenceEqual(Hash(3)));
    }

    [Fact]
    public async Task Personal_data_blocks_never_reach_the_provider_and_do_not_stall_the_walk()
    {
        var (indexer, store, provider) = Create(batch: 2);
        store.Blocks.Add(new PendingBlock(_doc1, 0, "```bazaar-encrypted\nsubject: x\nciphertext: abc\n```", Hash(1)));
        store.Blocks.Add(new PendingBlock(_doc1, 1, "~~~bazaar-encrypted\nciphertext: def\n~~~", Hash(2)));
        store.Blocks.Add(new PendingBlock(_doc1, 2, "plain note text", Hash(3)));

        var result = await indexer.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, result.SkippedPersonal);
        Assert.Equal(1, result.Embedded);
        Assert.Equal(["plain note text"], provider.Seen);
        Assert.DoesNotContain((_doc1, 0), store.Stored.Keys);
        Assert.DoesNotContain((_doc1, 1), store.Stored.Keys);
    }

    [Fact]
    public async Task A_provider_failure_writes_nothing_and_reports_the_error()
    {
        var (indexer, store, provider) = Create();
        provider.Fail = true;
        store.Blocks.Add(new PendingBlock(_doc1, 0, "text", Hash(1)));

        var result = await indexer.RunOnceAsync(CancellationToken.None);

        Assert.Equal("provider down", result.Error);
        Assert.Empty(store.Stored);

        provider.Fail = false;
        Assert.Equal(1, (await indexer.RunOnceAsync(CancellationToken.None)).Embedded);
    }

    [Fact]
    public async Task A_block_that_is_too_long_is_skipped_and_the_rest_of_the_batch_is_embedded()
    {
        var profile = new EmbeddingProfile(Guid.NewGuid(), "fake", FakeEmbeddingProvider.DefaultModelId, 8, DateTimeOffset.UnixEpoch, true);
        var provider = new TooLongProvider(new FakeEmbeddingProvider(), "huge");
        var selector = new ActiveEmbeddingSelector(new FakeSettingSource("fake"), new FakeProfileStore(profile), new FakeRegistry(("fake", provider)));
        var store = new InMemoryIndexStore();
        store.Blocks.Add(new PendingBlock(_doc1, 0, "fine", Hash(1)));
        store.Blocks.Add(new PendingBlock(_doc1, 1, "huge", Hash(2)));
        store.Blocks.Add(new PendingBlock(_doc1, 2, "also fine", Hash(3)));

        var result = await new EmbeddingIndexer(selector, store, new FencedPersonalDataBlockDetector(), 8).RunOnceAsync(CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Embedded);
        Assert.Equal(1, result.TooLong);
        Assert.Equal(2, store.Stored.Count);
    }

    [Fact]
    public async Task A_retry_later_failure_ends_the_sweep_with_the_message()
    {
        var (indexer, store, provider) = Create();
        provider.FailWith = new EmbeddingRetryLaterException("route unset");
        store.Blocks.Add(new PendingBlock(_doc1, 0, "text", Hash(1)));

        var result = await indexer.RunOnceAsync(CancellationToken.None);

        Assert.Equal("route unset", result.Error);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task A_profile_mismatch_is_reported_and_nothing_is_embedded()
    {
        var profile = new EmbeddingProfile(Guid.NewGuid(), "fake", FakeEmbeddingProvider.DefaultModelId, 8, DateTimeOffset.UnixEpoch, true);
        var selector = new ActiveEmbeddingSelector(
            new FakeSettingSource("fake"), new FakeProfileStore(profile), new FakeRegistry(("fake", new FakeEmbeddingProvider(16))));
        var store = new InMemoryIndexStore();
        store.Blocks.Add(new PendingBlock(_doc1, 0, "text", Hash(1)));
        var indexer = new EmbeddingIndexer(selector, store, new FencedPersonalDataBlockDetector());

        var result = await indexer.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(store.Stored);
        Assert.Equal(0, store.ListCalls);
    }

    [Theory]
    [InlineData("```bazaar-encrypted\nx\n```", true)]
    [InlineData("  \n~~~ bazaar-encrypted\nx", true)]
    [InlineData("```csharp\nvar x;\n```", false)]
    [InlineData("A paragraph mentioning bazaar-encrypted in prose.", false)]
    [InlineData("``bazaar-encrypted", false)]
    public void Detector_recognises_only_the_encrypted_fence(string body, bool expected)
    {
        Assert.Equal(expected, new FencedPersonalDataBlockDetector().IsPersonalData(body));
    }

    [Fact]
    public void Vector_literal_is_pgvector_text()
    {
        Assert.Equal("[0.5,-1,2]", PostgresEmbeddingIndexStore.ToVectorLiteral([0.5f, -1f, 2f]));
        Assert.Throws<ArgumentException>(() => PostgresEmbeddingIndexStore.ToVectorLiteral([float.NaN]));
    }
}
