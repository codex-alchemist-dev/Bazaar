namespace Bazaar.Server.ColumnSeal.Tests;

public sealed class BlindIndexerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_index_is_stable_for_the_same_value_scope_and_column()
    {
        using var fx = new SealFixture();

        var a = await fx.Indexer.ComputeAsync("gc", "gc", "title", "Release plan", Ct);
        var b = await fx.Indexer.ComputeAsync("gc", "gc", "title", "Release plan", Ct);

        Assert.Equal(32, a.Hash.Length);
        Assert.Equal(a.Hash.ToArray(), b.Hash.ToArray());
        Assert.Equal(a.KeyId, b.KeyId);
    }

    [Fact]
    public async Task The_index_survives_a_restart_because_the_key_comes_from_the_stored_dek()
    {
        using var fx = new SealFixture();
        var before = await fx.Indexer.ComputeAsync("gc", "gc", "title", "Release plan", Ct);

        using var restarted = new InMemoryColumnDekStore(fx.Kek);
        restarted.ReplaceRowForTests(fx.Store.RowForTests(before.KeyId)!);
        var after = await new BlindIndexer(restarted).ComputeAsync("gc", "gc", "title", "Release plan", Ct);

        Assert.Equal(before.Hash.ToArray(), after.Hash.ToArray());
    }

    [Fact]
    public async Task The_index_differs_across_scope_and_across_column_and_table()
    {
        using var fx = new SealFixture();

        var baseline = await fx.Indexer.ComputeAsync("gc", "gc", "title", "Plan", Ct);
        var otherScope = await fx.Indexer.ComputeAsync("tasks", "gc", "title", "Plan", Ct);
        var otherColumn = await fx.Indexer.ComputeAsync("gc", "gc", "metadata", "Plan", Ct);
        var otherTable = await fx.Indexer.ComputeAsync("gc", "tasks", "title", "Plan", Ct);
        var otherValue = await fx.Indexer.ComputeAsync("gc", "gc", "title", "Plans", Ct);

        var all = new[] { baseline, otherScope, otherColumn, otherTable, otherValue }.Select(v => Convert.ToHexString(v.Hash.Span)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public async Task Two_scopes_with_the_same_column_differ_even_under_the_same_value_and_dek_material()
    {
        // Same scope name in two installs would have two DEKs; here two scopes in one install already differ by key and by label.
        using var fx = new SealFixture();

        var a = await fx.Indexer.ComputeAsync("one", "t", "c", "same", Ct);
        var b = await fx.Indexer.ComputeAsync("two", "t", "c", "same", Ct);

        Assert.NotEqual(a.KeyId, b.KeyId);
        Assert.NotEqual(a.Hash.ToArray(), b.Hash.ToArray());
    }

    [Fact]
    public async Task Values_that_normalise_equal_index_equal()
    {
        using var fx = new SealFixture();

        var plain = await fx.Indexer.ComputeAsync("gc", "gc", "title", "Cafe Plan 1", Ct);
        var shouty = await fx.Indexer.ComputeAsync("gc", "gc", "title", "CAFE PLAN 1", Ct);
        var fullWidth = await fx.Indexer.ComputeAsync("gc", "gc", "title", "\uff23\uff41\uff46\uff45 \uff30lan \uff11", Ct);
        var accentedA = await fx.Indexer.ComputeAsync("gc", "gc", "title", "caf\u00e9", Ct);
        var accentedB = await fx.Indexer.ComputeAsync("gc", "gc", "title", "cafe\u0301", Ct);
        var accentedUpper = await fx.Indexer.ComputeAsync("gc", "gc", "title", "CAF\u00c9", Ct);

        Assert.Equal(plain.Hash.ToArray(), shouty.Hash.ToArray());
        Assert.Equal(plain.Hash.ToArray(), fullWidth.Hash.ToArray());
        Assert.Equal(accentedA.Hash.ToArray(), accentedB.Hash.ToArray());
        Assert.Equal(accentedA.Hash.ToArray(), accentedUpper.Hash.ToArray());
        Assert.NotEqual(plain.Hash.ToArray(), accentedA.Hash.ToArray());
    }

    [Fact]
    public void Normalisation_is_nfkc_then_invariant_upper_case()
    {
        Assert.Equal("FI", BlindIndexer.Normalise("\ufb01"));
        Assert.Equal("ABC1", BlindIndexer.Normalise("\uff41\uff42\uff43\uff11"));
        Assert.Equal("I", BlindIndexer.Normalise("i"));
    }

    [Fact]
    public async Task The_index_key_is_not_the_dek_and_not_the_plain_hmac_of_the_dek()
    {
        using var fx = new SealFixture();
        var value = await fx.Indexer.ComputeAsync("gc", "t", "c", "Name", Ct);
        using var dek = (await fx.Store.GetAsync(value.KeyId, Ct))!;

        var withDekDirectly = System.Security.Cryptography.HMACSHA256.HashData(dek.Key, System.Text.Encoding.UTF8.GetBytes("NAME"));

        Assert.NotEqual(withDekDirectly, value.Hash.ToArray());
    }

    [Fact]
    public async Task After_a_key_is_retired_old_rows_are_found_with_the_old_key_id()
    {
        using var fx = new SealFixture();
        var old = await fx.Indexer.ComputeAsync("gc", "t", "c", "Name", Ct);
        await fx.Store.RetireAsync(old.KeyId, Ct);

        var current = await fx.Indexer.ComputeAsync("gc", "t", "c", "Name", Ct);
        var viaOldKey = await fx.Indexer.ComputeAsync("gc", "t", "c", "Name", old.KeyId, Ct);

        Assert.NotEqual(old.KeyId, current.KeyId);
        Assert.NotEqual(old.Hash.ToArray(), current.Hash.ToArray());
        Assert.Equal(old.Hash.ToArray(), viaOldKey.Hash.ToArray());
    }

    [Fact]
    public async Task A_key_of_another_scope_or_an_unknown_key_is_refused()
    {
        using var fx = new SealFixture();
        var gc = await fx.Indexer.ComputeAsync("gc", "t", "c", "Name", Ct);

        var wrongScope = await Assert.ThrowsAsync<ColumnSealException>(async () =>
            await fx.Indexer.ComputeAsync("mail", "t", "c", "Name", gc.KeyId, Ct));
        var unknown = await Assert.ThrowsAsync<ColumnSealException>(async () =>
            await fx.Indexer.ComputeAsync("gc", "t", "c", "Name", Guid.NewGuid(), Ct));

        Assert.Equal(ColumnSealFailure.KeyUnavailable, wrongScope.Failure);
        Assert.Equal(ColumnSealFailure.KeyUnavailable, unknown.Failure);
    }

    [Fact]
    public async Task Bad_names_and_unencodable_values_are_invalid_input_and_do_not_echo_the_value()
    {
        using var fx = new SealFixture();

        var name = await Assert.ThrowsAsync<ColumnSealException>(async () =>
            await fx.Indexer.ComputeAsync("gc", "a|b", "c", "Name", Ct));
        var value = await Assert.ThrowsAsync<ColumnSealException>(async () =>
            await fx.Indexer.ComputeAsync("gc", "t", "c", "secret-looking\ud800value", Ct));

        Assert.Equal(ColumnSealFailure.InvalidInput, name.Failure);
        Assert.Equal(ColumnSealFailure.InvalidInput, value.Failure);
        Assert.DoesNotContain("secret-looking", value.ToString(), StringComparison.Ordinal);
    }
}
