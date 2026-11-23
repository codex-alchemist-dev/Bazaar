using System.Security.Cryptography;

namespace Bazaar.Server.ColumnSeal.Tests;

public sealed class ColumnSealerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sealed_value_opens_to_the_same_bytes()
    {
        using var fx = new SealFixture();
        var plain = SealFixture.Random();
        var ctx = SealFixture.Context();

        var sealedValue = await fx.Sealer.SealAsync(plain, ctx, Ct);
        var opened = await fx.Sealer.OpenAsync(sealedValue, ctx, Ct);

        Assert.Equal(plain, opened);
        Assert.Equal(12, sealedValue.Nonce.Length);
        Assert.Equal(plain.Length + 16, sealedValue.Ciphertext.Length);
    }

    [Fact]
    public async Task An_empty_value_round_trips()
    {
        using var fx = new SealFixture();
        var ctx = SealFixture.Context();

        var sealedValue = await fx.Sealer.SealAsync(ReadOnlyMemory<byte>.Empty, ctx, Ct);

        Assert.Empty(await fx.Sealer.OpenAsync(sealedValue, ctx, Ct));
    }

    [Fact]
    public async Task Two_seals_of_the_same_plaintext_differ()
    {
        using var fx = new SealFixture();
        var plain = SealFixture.Random();
        var ctx = SealFixture.Context();

        var a = await fx.Sealer.SealAsync(plain, ctx, Ct);
        var b = await fx.Sealer.SealAsync(plain, ctx, Ct);

        Assert.NotEqual(a.Nonce.ToArray(), b.Nonce.ToArray());
        Assert.NotEqual(a.Ciphertext.ToArray(), b.Ciphertext.ToArray());
    }

    [Fact]
    public async Task A_tampered_ciphertext_or_nonce_does_not_open()
    {
        using var fx = new SealFixture();
        var ctx = SealFixture.Context();
        var sealedValue = await fx.Sealer.SealAsync(SealFixture.Random(), ctx, Ct);

        var cipher = sealedValue.Ciphertext.ToArray();
        cipher[0] ^= 1;
        var tag = sealedValue.Ciphertext.ToArray();
        tag[^1] ^= 1;
        var nonce = sealedValue.Nonce.ToArray();
        nonce[3] ^= 1;

        await AssertCannotOpen(fx, sealedValue with { Ciphertext = cipher }, ctx);
        await AssertCannotOpen(fx, sealedValue with { Ciphertext = tag }, ctx);
        await AssertCannotOpen(fx, sealedValue with { Nonce = nonce }, ctx);
        await AssertCannotOpen(fx, sealedValue with { Nonce = nonce.AsMemory(0, 11) }, ctx);
        await AssertCannotOpen(fx, sealedValue with { Ciphertext = cipher.AsMemory(0, 10) }, ctx);
    }

    [Fact]
    public async Task A_value_does_not_open_under_another_table_column_row_or_scope()
    {
        using var fx = new SealFixture();
        var ctx = SealFixture.Context();
        var sealedValue = await fx.Sealer.SealAsync(SealFixture.Random(), ctx, Ct);

        await AssertCannotOpen(fx, sealedValue, ctx with { Table = "gc_events" });
        await AssertCannotOpen(fx, sealedValue, ctx with { Column = "payloads" });
        await AssertCannotOpen(fx, sealedValue, ctx with { RowKey = "row-2" });
        await AssertCannotOpen(fx, sealedValue, ctx with { Scope = "other" });
    }

    [Fact]
    public async Task A_value_sealed_for_one_row_cannot_be_opened_as_another()
    {
        using var fx = new SealFixture();
        var plain = SealFixture.Random();
        var rowA = SealFixture.Context("a");
        var rowB = SealFixture.Context("b");
        var sealedA = await fx.Sealer.SealAsync(plain, rowA, Ct);
        var sealedB = await fx.Sealer.SealAsync(plain, rowB, Ct);

        await AssertCannotOpen(fx, sealedA, rowB);
        await AssertCannotOpen(fx, sealedB, rowA);
        Assert.Equal(plain, await fx.Sealer.OpenAsync(sealedA, rowA, Ct));
    }

    [Fact]
    public async Task A_changed_key_id_does_not_open()
    {
        using var fx = new SealFixture();
        var ctx = SealFixture.Context();
        var sealedValue = await fx.Sealer.SealAsync(SealFixture.Random(), ctx, Ct);

        // A second key in the same scope: the value is bound to the first key id.
        using var second = await CreateSecondKeyAsync(fx, ctx.Scope);

        var ex = await Assert.ThrowsAsync<ColumnSealException>(async () =>
            await fx.Sealer.OpenAsync(sealedValue with { KeyId = second.DekId }, ctx, Ct));
        Assert.Equal(ColumnSealFailure.CannotOpen, ex.Failure);

        var unknown = await Assert.ThrowsAsync<ColumnSealException>(async () =>
            await fx.Sealer.OpenAsync(sealedValue with { KeyId = Guid.NewGuid() }, ctx, Ct));
        Assert.Equal(ColumnSealFailure.KeyUnavailable, unknown.Failure);
    }

    [Fact]
    public async Task A_retired_key_still_opens_old_values_but_new_seals_use_a_new_key()
    {
        using var fx = new SealFixture();
        var plain = SealFixture.Random();
        var ctx = SealFixture.Context();
        var old = await fx.Sealer.SealAsync(plain, ctx, Ct);

        Assert.True(await fx.Store.RetireAsync(old.KeyId, Ct));
        var fresh = await fx.Sealer.SealAsync(plain, ctx, Ct);

        Assert.NotEqual(old.KeyId, fresh.KeyId);
        Assert.Equal(plain, await fx.Sealer.OpenAsync(old, ctx, Ct));
        Assert.Equal(plain, await fx.Sealer.OpenAsync(fresh, ctx, Ct));
        Assert.False(await fx.Store.RetireAsync(old.KeyId, Ct));
    }

    [Fact]
    public async Task Each_scope_has_its_own_key()
    {
        using var fx = new SealFixture();

        var a = await fx.Sealer.SealAsync(SealFixture.Random(), SealFixture.Context(scope: "gc"), Ct);
        var b = await fx.Sealer.SealAsync(SealFixture.Random(), SealFixture.Context(scope: "mail"), Ct);
        var again = await fx.Sealer.SealAsync(SealFixture.Random(), SealFixture.Context(scope: "gc"), Ct);

        Assert.NotEqual(a.KeyId, b.KeyId);
        Assert.Equal(a.KeyId, again.KeyId);
    }

    [Fact]
    public async Task A_key_survives_a_restart_of_the_process_through_the_store_rows()
    {
        using var fx = new SealFixture();
        var plain = SealFixture.Random();
        var ctx = SealFixture.Context();
        var sealedValue = await fx.Sealer.SealAsync(plain, ctx, Ct);

        // A second store over the same KEK, with the rows copied, has an empty cache: it must unwrap the key.
        using var restarted = new InMemoryColumnDekStore(fx.Kek);
        restarted.ReplaceRowForTests(fx.Store.RowForTests(sealedValue.KeyId)!);

        Assert.Equal(plain, await new ColumnSealer(restarted).OpenAsync(sealedValue, ctx, Ct));
    }

    [Fact]
    public async Task Invalid_names_are_rejected_before_any_key_is_touched()
    {
        using var fx = new SealFixture();
        var plain = SealFixture.Random();

        foreach (var bad in new[] { "", "a|b", "a\nb", "a\0b", "a\tb", "a\u2028b", "a\u200db", new string('x', 129), "a\ud800b" })
        {
            foreach (var ctx in new[]
            {
                SealFixture.Context(table: bad),
                SealFixture.Context(column: bad),
                SealFixture.Context(scope: bad),
            })
            {
                var ex = await Assert.ThrowsAsync<ColumnSealException>(async () => await fx.Sealer.SealAsync(plain, ctx, Ct));
                Assert.Equal(ColumnSealFailure.InvalidInput, ex.Failure);
            }
        }

        foreach (var badRow in new[] { "", new string('x', 513), "a\ud800b" })
        {
            var ex = await Assert.ThrowsAsync<ColumnSealException>(async () =>
                await fx.Sealer.SealAsync(plain, SealFixture.Context(row: badRow), Ct));
            Assert.Equal(ColumnSealFailure.InvalidInput, ex.Failure);
        }
    }

    [Fact]
    public async Task A_row_key_may_contain_a_separator_and_still_binds_to_its_row()
    {
        using var fx = new SealFixture();
        var plain = SealFixture.Random();
        var ctx = SealFixture.Context(row: "a|b|c");
        var sealedValue = await fx.Sealer.SealAsync(plain, ctx, Ct);

        Assert.Equal(plain, await fx.Sealer.OpenAsync(sealedValue, ctx, Ct));
        await AssertCannotOpen(fx, sealedValue, ctx with { RowKey = "a|b" });
        await AssertCannotOpen(fx, sealedValue, ctx with { RowKey = "a" });
    }

    [Fact]
    public async Task A_wrapped_key_cannot_be_unwrapped_under_another_scope()
    {
        using var fx = new SealFixture();
        var ctx = SealFixture.Context(scope: "gc");
        var sealedValue = await fx.Sealer.SealAsync(SealFixture.Random(), ctx, Ct);
        var row = fx.Store.RowForTests(sealedValue.KeyId)!;

        // The same wrapped bytes under a different scope label, as an attacker with database write access would store them.
        using var attacked = new InMemoryColumnDekStore(fx.Kek);
        attacked.ReplaceRowForTests(row with { Scope = "mail" });

        var ex = await Assert.ThrowsAsync<ColumnSealException>(async () =>
            await new ColumnSealer(attacked).OpenAsync(sealedValue, SealFixture.Context(scope: "mail"), Ct));
        Assert.Equal(ColumnSealFailure.KeyUnavailable, ex.Failure);

        // And under a different dek id.
        var moved = row with { DekId = Guid.NewGuid() };
        using var moved2 = new InMemoryColumnDekStore(fx.Kek);
        moved2.ReplaceRowForTests(moved);
        var ex2 = await Assert.ThrowsAsync<ColumnSealException>(async () => await moved2.GetAsync(moved.DekId, Ct));
        Assert.Equal(ColumnSealFailure.KeyUnavailable, ex2.Failure);
    }

    [Fact]
    public async Task No_exception_message_holds_plaintext_or_key_bytes()
    {
        using var fx = new SealFixture();
        var plain = SealFixture.Random(64);
        var ctx = SealFixture.Context(row: Convert.ToHexString(SealFixture.Random(12)));
        var sealedValue = await fx.Sealer.SealAsync(plain, ctx, Ct);
        var dek = (await fx.Store.GetAsync(sealedValue.KeyId, Ct))!;
        var keyHex = Convert.ToHexString(dek.Key);
        var plainHex = Convert.ToHexString(plain);
        var wrappedHex = Convert.ToHexString(fx.Store.RowForTests(sealedValue.KeyId)!.WrappedDek);
        dek.Dispose();

        var tampered = sealedValue.Ciphertext.ToArray();
        tampered[0] ^= 1;
        var failures = new List<ColumnSealException>
        {
            await Assert.ThrowsAsync<ColumnSealException>(async () => await fx.Sealer.OpenAsync(sealedValue with { Ciphertext = tampered }, ctx, Ct)),
            await Assert.ThrowsAsync<ColumnSealException>(async () => await fx.Sealer.OpenAsync(sealedValue, ctx with { RowKey = "other" }, Ct)),
            await Assert.ThrowsAsync<ColumnSealException>(async () => await fx.Sealer.OpenAsync(sealedValue with { KeyId = Guid.NewGuid() }, ctx, Ct)),
            await Assert.ThrowsAsync<ColumnSealException>(async () => await fx.Sealer.SealAsync(plain, ctx with { Table = "a|b" }, Ct)),
        };

        foreach (var failure in failures)
        {
            var text = failure.ToString();
            Assert.DoesNotContain(keyHex, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(plainHex, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(wrappedHex, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ctx.RowKey, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Concurrent_first_seals_for_a_scope_end_up_on_one_key()
    {
        using var fx = new SealFixture();
        var ctx = SealFixture.Context();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(
            async () => await fx.Sealer.SealAsync(SealFixture.Random(), ctx, Ct), Ct)));

        Assert.Single(results.Select(r => r.KeyId).Distinct());
    }

    private static async Task<ColumnDek> CreateSecondKeyAsync(SealFixture fx, string scope)
    {
        var first = await fx.Store.GetOrCreateActiveAsync(scope, TestContext.Current.CancellationToken);
        await fx.Store.RetireAsync(first.DekId, TestContext.Current.CancellationToken);
        first.Dispose();
        return await fx.Store.GetOrCreateActiveAsync(scope, TestContext.Current.CancellationToken);
    }

    private static async Task AssertCannotOpen(SealFixture fx, SealedColumnValue value, ColumnSealContext ctx)
    {
        var ex = await Assert.ThrowsAsync<ColumnSealException>(async () => await fx.Sealer.OpenAsync(value, ctx, TestContext.Current.CancellationToken));
        Assert.Equal(ColumnSealFailure.CannotOpen, ex.Failure);
        Assert.IsNotType<CryptographicException>(ex);
    }
}
