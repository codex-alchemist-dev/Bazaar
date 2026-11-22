using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.Identity.Kek.Tests;

/// <summary>
/// The start-up custody check (chaos finding 1 of the siege log): a host whose key file is not the one the database's keys were
/// wrapped under must be told apart from a first start and from a half-finished provider switch. Database-free.
/// </summary>
public sealed class KekCustodyCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-custody-" + Guid.CreateVersion7().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class MemoryStore : IKekRewrapStore
    {
        public readonly List<KekWrapRecord> Rows = [];

        public int Lists { get; private set; }

        public Task<IReadOnlyList<KekWrapRecord>> ListLiveWrapsAsync(Guid? afterId, int limit, CancellationToken ct)
        {
            Lists++;
            IReadOnlyList<KekWrapRecord> page = Rows
                .Where(r => afterId is null || r.Id.CompareTo(afterId.Value) > 0)
                .OrderBy(r => r.Id)
                .Take(limit)
                .ToList();
            return Task.FromResult(page);
        }

        public Task<bool> ReplaceWrapAsync(Guid id, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> replacement, string kekId, CancellationToken ct) =>
            throw new InvalidOperationException("The custody check must not write.");
    }

    private IKekProvider Kek(string dataRoot) => KekProviderFactory.Create("file", Path.Combine(_root, dataRoot));

    private static async Task AddAsync(MemoryStore store, IKekProvider kek, int count)
    {
        for (var i = 0; i < count; i++)
        {
            // Ids rise in the order rows are added, so a test controls which rows the walk meets first.
            var id = new Guid(store.Rows.Count + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var wrapped = await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), id.ToByteArray(), Ct);
            store.Rows.Add(new KekWrapRecord(id, wrapped, id.ToByteArray()));
        }
    }

    [Fact]
    public async Task A_database_with_no_wrapped_keys_passes_and_never_opens_the_key_file()
    {
        var opened = 0;
        var check = new KekCustodyCheck(
            () =>
            {
                opened++;
                return Kek("a");
            },
            () => [new MemoryStore(), new MemoryStore()]);

        var result = await check.CheckAsync(Ct);

        Assert.True(result.Matches);
        Assert.Equal(0, result.Stored);
        Assert.Equal(0, opened);
        Assert.False(Directory.Exists(Path.Combine(_root, "a")));
    }

    [Fact]
    public async Task The_key_the_database_was_set_up_with_passes_at_the_first_row()
    {
        var kek = Kek("a");
        var store = new MemoryStore();
        await AddAsync(store, kek, 600);

        var result = await new KekCustodyCheck(kek, [store]).CheckAsync(Ct);

        Assert.True(result.Matches);
        Assert.Equal(1, result.Stored);
        Assert.Equal(1, store.Lists);
    }

    [Fact]
    public async Task A_key_minted_in_another_data_root_opens_nothing_and_is_refused()
    {
        var store = new MemoryStore();
        await AddAsync(store, Kek("mounted"), 600);

        // The data root was not mounted: the host found no key file there and made a new one.
        var result = await new KekCustodyCheck(Kek("empty-mount-point"), [new MemoryStore(), store]).CheckAsync(Ct);

        Assert.False(result.Matches);
        Assert.Equal(600, result.Stored);
        Assert.Equal(0, result.Opened);
    }

    [Fact]
    public async Task A_half_finished_switch_to_another_key_still_passes_for_the_old_key()
    {
        var oldKek = Kek("old");
        var store = new MemoryStore();
        // The re-wrap walks ids upwards, so after an interruption the lowest ids are under the new key.
        await AddAsync(store, Kek("new"), 300);
        await AddAsync(store, oldKek, 5);

        var result = await new KekCustodyCheck(oldKek, [store]).CheckAsync(Ct);

        Assert.True(result.Matches);
        Assert.Equal(301, result.Stored);
    }

    [Fact]
    public async Task A_key_in_the_second_store_is_enough()
    {
        var kek = Kek("a");
        var first = new MemoryStore();
        var second = new MemoryStore();
        await AddAsync(first, Kek("b"), 3);
        await AddAsync(second, kek, 1);

        Assert.True((await new KekCustodyCheck(kek, [first, second]).CheckAsync(Ct)).Matches);
    }
}
