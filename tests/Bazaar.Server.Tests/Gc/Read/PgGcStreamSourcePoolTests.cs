using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Tests.Identity;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Read;

/// <summary>
/// Opening an encrypted body fetches its key through the connection pool. A read must not do that while it still holds a
/// connection, or as many overlapping replays as the pool has connections wait on each other until the pool times out.
/// </summary>
public sealed class PgGcStreamSourcePoolTests(GcDb db) : IClassFixture<GcDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_replays_of_encrypted_messages_do_not_exhaust_a_small_connection_pool()
    {
        GcTestSkip.WithoutDatabase();
        using var kek = new FakeKekProvider();
        var codec = new AesGcmGcBodyCodec(db.DataSource, kek);
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        for (var i = 0; i < 5; i++)
        {
            await writer.AppendAsync(
                new AppendRequest(gc, GcEventKinds.Message, AuthorKind.Human, author, SourceTrust.MemberHuman, $"k{i}")
                {
                    Message = await codec.SealAsync(author, $"message {i}", Ct),
                },
                Ct);
        }

        // Three connections for eight readers: with the nested key fetch every reader holds one and waits for another.
        var small = new NpgsqlConnectionStringBuilder(db.ConnectionString) { MaxPoolSize = 3, Timeout = 5, Pooling = true, ApplicationName = "pool-test" };
        await using var pool = NpgsqlDataSource.Create(small.ConnectionString);
        var source = new PgGcStreamSource(pool, new AesGcmGcBodyCodec(pool, kek));

        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await source.ReadAsync(gc, 0, 0, 0, 200, Ct).AsTask(), Ct)));

        Assert.All(reads, r => Assert.Equal(["message 0", "message 1", "message 2", "message 3", "message 4"], r.Select(m => m.Text)));
    }
}
