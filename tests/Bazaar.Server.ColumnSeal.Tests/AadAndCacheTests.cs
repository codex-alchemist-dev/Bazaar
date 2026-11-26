using System.Text;

namespace Bazaar.Server.ColumnSeal.Tests;

public sealed class AadAndCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_associated_data_has_the_documented_shape()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var aad = Encoding.UTF8.GetString(ColumnSealNames.BuildAad(new ColumnSealContext("t", "c", "r1", "gc"), id));

        Assert.Equal("bazaar.colseal.v1|t|c|2:r1|11111111-2222-3333-4444-555555555555", aad);
    }

    [Fact]
    public void Row_keys_that_collide_under_naive_joining_do_not_collide()
    {
        var id = Guid.NewGuid();
        // Naive joining "table|column|row|keyid" would make these pairs equal if separators were allowed in the row key.
        var one = ColumnSealNames.BuildAad(new ColumnSealContext("t", "c", "a|b", "gc"), id);
        var two = ColumnSealNames.BuildAad(new ColumnSealContext("t", "c", "a", "gc"), id);
        var three = ColumnSealNames.BuildAad(new ColumnSealContext("t", "c", $"a|{id:D}", "gc"), id);
        var four = ColumnSealNames.BuildAad(new ColumnSealContext("t", "c", "a", "gc"), id);

        Assert.NotEqual(one, two);
        Assert.NotEqual(three, four);
        // A row key that looks like a whole tail ("x|keyid") cannot stand in for a shorter row key plus the key id.
        var forged = ColumnSealNames.BuildAad(new ColumnSealContext("t", "c", "1:x", "gc"), id);
        var plain = ColumnSealNames.BuildAad(new ColumnSealContext("t", "c", "x", "gc"), id);
        Assert.NotEqual(forged, plain);
    }

    [Fact]
    public void The_row_key_length_counts_utf8_bytes()
    {
        var aad = Encoding.UTF8.GetString(ColumnSealNames.BuildAad(new ColumnSealContext("t", "c", "\u00e9\u20ac", "gc"), Guid.Empty));

        Assert.Contains("|5:\u00e9\u20ac|", aad, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a|b")]
    [InlineData("a\rb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u007fb")]
    [InlineData("a\u2029b")]
    public void Separators_and_control_characters_are_rejected_in_names(string name)
    {
        var ex = Assert.Throws<ColumnSealException>(() => ColumnSealNames.ValidateName(name, "table name"));

        Assert.Equal(ColumnSealFailure.InvalidInput, ex.Failure);
    }

    [Fact]
    public void The_dek_associated_data_binds_scope_and_id()
    {
        var id = Guid.NewGuid();

        Assert.NotEqual(ColumnSealNames.BuildDekAad("gc", id), ColumnSealNames.BuildDekAad("mail", id));
        Assert.NotEqual(ColumnSealNames.BuildDekAad("gc", id), ColumnSealNames.BuildDekAad("gc", Guid.NewGuid()));
    }

    [Fact]
    public void The_cache_zeroes_keys_on_dispose()
    {
        var cache = new DekCache(4);
        cache.Add(new ColumnDek("gc", Guid.NewGuid(), "kek", SealFixture.Random(32), retired: false));
        cache.Add(new ColumnDek("mail", Guid.NewGuid(), "kek", SealFixture.Random(32), retired: false));
        var arrays = cache.KeyArraysForTests();
        Assert.All(arrays, a => Assert.Contains(a, b => b != 0));

        cache.Dispose();

        Assert.Equal(2, arrays.Count);
        Assert.All(arrays, a => Assert.All(a, b => Assert.Equal(0, b)));
        Assert.Throws<ObjectDisposedException>(() => cache.TryGet(Guid.NewGuid()));
    }

    [Fact]
    public void The_cache_is_bounded_and_zeroes_an_evicted_key()
    {
        var cache = new DekCache(2);
        var first = Guid.NewGuid();
        cache.Add(new ColumnDek("a", first, "kek", SealFixture.Random(32), retired: false));
        var firstArray = cache.KeyArraysForTests().Single();
        cache.Add(new ColumnDek("b", Guid.NewGuid(), "kek", SealFixture.Random(32), retired: false));
        cache.Add(new ColumnDek("c", Guid.NewGuid(), "kek", SealFixture.Random(32), retired: false));

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.TryGet(first));
        Assert.All(firstArray, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Using_a_key_copy_after_eviction_still_works_and_disposing_it_leaves_the_cache_alone()
    {
        var cache = new DekCache(1);
        var id = Guid.NewGuid();
        cache.Add(new ColumnDek("a", id, "kek", SealFixture.Random(32), retired: false));
        var copy = cache.TryGet(id)!;

        cache.Add(new ColumnDek("b", Guid.NewGuid(), "kek", SealFixture.Random(32), retired: false));

        Assert.Contains(copy.Key.ToArray(), b => b != 0);
        copy.Dispose();
        Assert.All(copy.Key.ToArray(), b => Assert.Equal(0, b));
        cache.Dispose();
    }

    [Fact]
    public async Task The_store_cache_zeroes_on_dispose()
    {
        var fx = new SealFixture();
        _ = await fx.Sealer.SealAsync(SealFixture.Random(), SealFixture.Context(), Ct);
        var arrays = fx.Store.Cache.KeyArraysForTests();
        Assert.NotEmpty(arrays);

        fx.Dispose();

        Assert.All(arrays, a => Assert.All(a, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void The_component_is_the_documented_module()
    {
        var manifest = ColumnSealComponent.ManifestDefinition;

        Assert.Equal("column-seal", manifest.Id);
        Assert.Equal(["identity-kek"], manifest.DependsOn);
        Assert.Equal("column_seal", manifest.MigrationNamespace);
    }

    [Fact]
    public void The_component_rejects_a_bad_cache_size()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationManager { [ColumnSealComponent.CacheSizeKey] = "0" };

        Assert.Throws<InvalidOperationException>(() => ColumnSealComponent.ReadOptions(config));
    }
}
