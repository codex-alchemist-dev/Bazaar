using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc;

/// <summary>
/// Crash-only fault test for the GC log (doc 46 sections 7.2 and 17.1; doc 02 idempotency). The chaos campaign killed the host with
/// SIGKILL while clients appended; this is its repeatable form: the writers' database sessions are terminated at random moments, so
/// some commits are lost and some succeed with the answer lost. Every writer retries with the same idempotency key, as a client does
/// after a reconnect. The log must stay gap-free, keep every acknowledged append at its acknowledged seq, and hold each key once.
/// </summary>
public sealed class GcLogWriterKillTests(GcDb db) : IClassFixture<GcDb>
{
    private const int _writers = 6;
    private const int _appendsPerWriter = 40;

    [Fact]
    public async Task Writers_whose_sessions_are_killed_mid_append_leave_a_gap_free_log_with_every_acked_append_once()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var appName = "gc-kill-" + Guid.NewGuid().ToString("N")[..12];
        await using var victims = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString)
        {
            ApplicationName = appName,
            MaxPoolSize = _writers + 2,
        }.ConnectionString);
        var writer = new GcLogWriter(victims, new RecordingNotifier());
        var acked = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        var retries = 0;
        var killed = 0;
        using var stopKiller = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var killer = Task.Run(async () =>
        {
            var random = new Random(4242);
            while (!stopKiller.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(random.Next(2, 25), stopKiller.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await using var cmd = db.DataSource.CreateCommand(
                    "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = $1 AND state <> 'idle'");
                cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appName });
                Interlocked.Add(ref killed, (int)(long)(await cmd.ExecuteScalarAsync(CancellationToken.None))!);
            }
        }, CancellationToken.None);

        var writing = Enumerable.Range(0, _writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < _appendsPerWriter; i++)
            {
                var key = $"w{w}-{i}";
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var result = await writer.AppendAsync(GcTestData.System(gc, key), ct);
                        acked[key] = result.Seq;
                        break;
                    }
                    catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException && attempt < 200)
                    {
                        // The session died: the commit may or may not have happened. Retry with the same key.
                        Interlocked.Increment(ref retries);
                    }
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(writing);
        await stopKiller.CancelAsync();
        await killer;

        const long total = _writers * _appendsPerWriter;
        Assert.True(killed > 0 && retries > 0, $"no fault was injected (killed {killed}, retries {retries})");
        Assert.Equal(total, acked.Count);
        Assert.Equal(total, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
        Assert.Equal(total, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
        Assert.Equal(total, await db.ScalarAsync<long>("SELECT count(DISTINCT seq) FROM gc_event WHERE gc_id = $1 AND seq BETWEEN 1 AND $2", gc, total));
        Assert.Equal(total, await db.ScalarAsync<long>("SELECT count(DISTINCT client_msg_id) FROM gc_event WHERE gc_id = $1", gc));

        await using var stored = db.DataSource.CreateCommand("SELECT client_msg_id, seq FROM gc_event WHERE gc_id = $1");
        stored.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        await using var reader = await stored.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            Assert.Equal(acked[reader.GetString(0)], reader.GetInt64(1));
        }
    }
}
