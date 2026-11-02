using Bazaar.Server.Security.Readers;

namespace Bazaar.Server.Tests.Security.Readers;

public class InMemoryReaderScratchStoreTests
{
    private static readonly Guid _reader = Guid.Parse("0196a6f0-0000-7000-8000-000000000401");
    private static readonly Guid _other = Guid.Parse("0196a6f0-0000-7000-8000-000000000402");

    [Fact]
    public async Task Items_are_stored_and_read_back_per_session()
    {
        var store = new InMemoryReaderScratchStore();

        await store.PutAsync(_reader, "note", "first", ReaderTestData.Ct);
        await store.PutAsync(_other, "note", "other", ReaderTestData.Ct);

        Assert.Equal("first", await store.GetAsync(_reader, "note", ReaderTestData.Ct));
        Assert.Equal("other", await store.GetAsync(_other, "note", ReaderTestData.Ct));
        Assert.Null(await store.GetAsync(_reader, "missing", ReaderTestData.Ct));
    }

    [Fact]
    public async Task Purge_removes_every_item_of_the_session()
    {
        var store = new InMemoryReaderScratchStore();
        await store.PutAsync(_reader, "a", "1", ReaderTestData.Ct);
        await store.PutAsync(_reader, "b", "2", ReaderTestData.Ct);

        await store.PurgeAsync(_reader, ReaderTestData.Ct);

        Assert.Equal(0, store.CountItems(_reader));
        Assert.Null(await store.GetAsync(_reader, "a", ReaderTestData.Ct));
        Assert.Null(await store.GetAsync(_reader, "b", ReaderTestData.Ct));
    }

    [Fact]
    public async Task Purge_leaves_other_sessions_alone()
    {
        var store = new InMemoryReaderScratchStore();
        await store.PutAsync(_reader, "a", "1", ReaderTestData.Ct);
        await store.PutAsync(_other, "a", "kept", ReaderTestData.Ct);

        await store.PurgeAsync(_reader, ReaderTestData.Ct);

        Assert.Equal("kept", await store.GetAsync(_other, "a", ReaderTestData.Ct));
    }

    [Fact]
    public async Task A_purged_session_refuses_later_writes_and_stays_empty()
    {
        var store = new InMemoryReaderScratchStore();
        await store.PurgeAsync(_reader, ReaderTestData.Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutAsync(_reader, "late", "x", ReaderTestData.Ct));
        Assert.Equal(0, store.CountItems(_reader));
    }

    [Fact]
    public async Task Purge_can_run_twice()
    {
        var store = new InMemoryReaderScratchStore();
        await store.PutAsync(_reader, "a", "1", ReaderTestData.Ct);

        await store.PurgeAsync(_reader, ReaderTestData.Ct);
        await store.PurgeAsync(_reader, ReaderTestData.Ct);

        Assert.Equal(0, store.CountItems(_reader));
    }
}
