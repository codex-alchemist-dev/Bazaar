// Spike S3: GC log at scale on Postgres 18. Throwaway harness (see docs/spikes/S3.md).
// Usage: S3 --run N [--parts 16] [--notify on|off] [--tsindex on|off] [--phases load,hot,page,replay,index,reconnect]
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;

var a = args.Select((x, i) => (x, i)).Where(t => t.x.StartsWith("--")).ToDictionary(t => t.x[2..], t => t.i + 1 < args.Length ? args[t.i + 1] : "");
string Arg(string k, string d) => a.TryGetValue(k, out var v) ? v : d;
int run = int.Parse(Arg("run", "1"));
int parts = int.Parse(Arg("parts", "16"));
string notifyMode = Arg("notify", "on"); // on = pg_notify inside the append tx; post = separate statement after commit; off
bool notify = notifyMode == "on";
bool tsIndex = Arg("tsindex", "off") == "on";
var phases = Arg("phases", "load,hot,page,replay,index,reconnect").Split(',').ToHashSet();
string host = Arg("host", "127.0.0.1"), port = Arg("port", "54318");
string dbName = $"s3_r{run}_p{parts}";
double hotShare = double.Parse(Arg("hotshare", "0.30"));
int pacedRate = int.Parse(Arg("rate", "500")), pacedSecs = int.Parse(Arg("secs", "30"));

string Cs(string db, string extra = "") => $"Host={host};Port={port};Username=postgres;Password=spike;Database={db};Maximum Pool Size=100;Minimum Pool Size=8;{extra}";
var result = new SortedDictionary<string, object> { ["run"] = run, ["parts"] = parts, ["notify"] = notifyMode, ["hotshare"] = hotShare, ["tsindex"] = tsIndex };

// ---- database setup ----
bool fresh; bool reuse = Arg("reuse", "off") == "on"; // keep the preloaded database from an earlier invocation (same run id and partition count)
await using (var admin = new NpgsqlDataSourceBuilder(Cs("postgres")).Build())
{
    bool exists = await Scalar<long>(admin, $"SELECT count(*) FROM pg_database WHERE datname = '{dbName}'") > 0;
    fresh = !(reuse && exists);
    if (fresh)
    {
        await Exec(admin, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        await Exec(admin, $"CREATE DATABASE {dbName}");
    }
}
await using var ds = new NpgsqlDataSourceBuilder(Cs(dbName)).Build();
Guid GcId(int i) => Guid.Parse(Hex($"gc{i}"));
const int GcCount = 1000, HotEvents = 100_000, ColdEvents = 901;
var hot = GcId(0);
var principal = Guid.Parse(Hex("principal"));
var agents = Enumerable.Range(0, 20).Select(i => Guid.Parse(Hex($"agent{i}"))).ToArray();

if (fresh)
{
var schema = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema.sql"))).Replace("__PARTS__", parts.ToString());
await Exec(ds, schema);
if (tsIndex) await Exec(ds, "CREATE INDEX gc_event_gc_ts ON gc_event (gc_id, created_at)");

// ---- preload: 1000 GCs, ~1M events, one 100k-event hot GC ----
var sw = Stopwatch.StartNew();
await Exec(ds, "INSERT INTO gc(gc_id) SELECT md5('gc'||i)::uuid FROM generate_series(0,999) i");
await Exec(ds, $@"
CREATE TEMP TABLE n AS SELECT i, md5('gc'||i)::uuid gc_id, CASE WHEN i=0 THEN {HotEvents} ELSE {ColdEvents} END cnt FROM generate_series(0,999) i;
INSERT INTO gc_message(gc_id, message_id, first_seq, author_id)
  SELECT n.gc_id, md5(n.gc_id::text||s)::uuid, s, md5('agent'||(s%20))::uuid FROM n, LATERAL generate_series(1, n.cnt) s;
INSERT INTO gc_message_version(gc_id, message_id, version, body_ct, editor_id, created_at)
  SELECT n.gc_id, md5(n.gc_id::text||s)::uuid, 1, convert_to(repeat(md5(s::text),12),'UTF8'), md5('agent'||(s%20))::uuid, now() - interval '30 days' + s * interval '20 seconds'
  FROM n, LATERAL generate_series(1, n.cnt) s;
INSERT INTO gc_event(gc_id, seq, event_id, kind, author_id, message_id, version, client_msg_id, created_at)
  SELECT n.gc_id, s, gen_random_uuid(), 'message', md5('agent'||(s%20))::uuid, md5(n.gc_id::text||s)::uuid, 1, gen_random_uuid(), now() - interval '30 days' + s * interval '20 seconds'
  FROM n, LATERAL generate_series(1, n.cnt) s;
INSERT INTO gc_mention(gc_id, seq, target_principal_id)
  SELECT n.gc_id, s, md5('principal')::uuid FROM n, LATERAL generate_series(10, n.cnt, 10) s;
UPDATE gc SET head_seq = n.cnt FROM n WHERE gc.gc_id = n.gc_id;");
await Exec(ds, "ANALYZE");
if (Arg("checkpoint", "on") == "on") await Exec(ds, "CHECKPOINT");
result["preload_s"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
result["preload_events"] = await Scalar<long>(ds, "SELECT count(*) FROM gc_event");
result["db_size_mb"] = await Scalar<long>(ds, "SELECT pg_database_size(current_database())/1048576");
Console.WriteLine($"preload done {sw.Elapsed.TotalSeconds:F1}s events={result["preload_events"]} size={result["db_size_mb"]}MB");

}
else result["preload_events"] = await Scalar<long>(ds, "SELECT count(*) FROM gc_event");

// ---- append (single statement = one transaction, one round trip) ----
string appendSql = @"
WITH s AS (UPDATE gc SET head_seq = head_seq + 1 WHERE gc_id = $1 RETURNING head_seq),
e AS (INSERT INTO gc_event(gc_id, seq, event_id, kind, author_id, message_id, version, client_msg_id, created_at)
      SELECT $1, head_seq, $2, 'message', $3, $4, 1, $5, clock_timestamp() FROM s RETURNING seq),
m AS (INSERT INTO gc_message(gc_id, message_id, first_seq, author_id) SELECT $1, $4, seq, $3 FROM e),
v AS (INSERT INTO gc_message_version(gc_id, message_id, version, body_ct, editor_id, created_at) VALUES ($1, $4, 1, $6, $3, clock_timestamp())),
mn AS (INSERT INTO gc_mention(gc_id, seq, target_principal_id) SELECT $1, seq, $7 FROM e WHERE $8)
SELECT seq" + (notify ? ", pg_notify('gc', json_build_object('gc', $1, 'seq', seq)::text)" : "") + " FROM e";
var Pending = new ConcurrentDictionary<Guid, long>();
var rng = new Random(run * 7919 + parts);
var bodyBytes = new byte[400];
rng.NextBytes(bodyBytes);

async Task<long> Append(Guid gc, int agent, bool mention = false)
{
    await using var cmd = ds.CreateCommand(appendSql);
    cmd.Parameters.AddWithValue(gc);
    cmd.Parameters.AddWithValue(Guid.NewGuid());
    cmd.Parameters.AddWithValue(agents[agent % agents.Length]);
    cmd.Parameters.AddWithValue(Guid.NewGuid());
    cmd.Parameters.AddWithValue(Guid.NewGuid());
    cmd.Parameters.AddWithValue(bodyBytes);
    cmd.Parameters.AddWithValue(principal);
    cmd.Parameters.AddWithValue(mention);
    long seq = (long)(await cmd.ExecuteScalarAsync())!;
    if (notifyMode == "batch") Pending[gc] = seq; // coalesced by the single notifier loop below
    if (notifyMode == "post")
    {
        await using var n = ds.CreateCommand("SELECT pg_notify('gc', json_build_object('gc', $1, 'seq', $2)::text)");
        n.Parameters.AddWithValue(gc); n.Parameters.AddWithValue(seq);
        await n.ExecuteNonQueryAsync();
    }
    return seq;
}
var notifierStop = new CancellationTokenSource();
var notifierTask = notifyMode != "batch" ? Task.CompletedTask : Task.Run(async () =>
{
    // One notifier: every 5 ms send the newest seq per GC in one transaction (one commit, one lock hold) instead of one per append.
    while (!notifierStop.IsCancellationRequested)
    {
        await Task.Delay(5);
        var batch = new List<string>();
        foreach (var k in Pending.Keys.ToArray()) if (Pending.TryRemove(k, out var sq)) batch.Add(JsonSerializer.Serialize(new { gc = k, seq = sq }));
        if (batch.Count == 0) continue;
        await using var c = ds.CreateCommand("SELECT pg_notify('gc', x) FROM unnest($1::text[]) x"); c.Parameters.AddWithValue(batch.ToArray()); await c.ExecuteNonQueryAsync();
    }
});
Guid PickGc(Random r) => r.NextDouble() < hotShare ? hot : GcId(1 + r.Next(GcCount - 1));

// ---- load: paced open loop; latency measured from scheduled start (no coordinated omission) ----
async Task<(double[] lat, double achieved, long errors)> Paced(int rate, int secs, Func<Random, Guid> pick, Func<Guid, long, double, Task>? onDone = null)
{
    var lat = new ConcurrentBag<double>(); long errors = 0;
    var tasks = new List<Task>();
    var clock = Stopwatch.StartNew();
    long total = (long)rate * secs, issued = 0;
    var tl = new ThreadLocal<Random>(() => new Random(Environment.CurrentManagedThreadId * 31 + run));
    while (issued < total)
    {
        double due = issued * 1000.0 / rate;
        double now = clock.Elapsed.TotalMilliseconds;
        if (now < due) { await Task.Delay(1); continue; }
        var gc = pick(tl.Value!); int ag = (int)(issued % 20); double sched = due;
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                long seq = await Append(gc, ag, ag % 5 == 0);
                double ms = clock.Elapsed.TotalMilliseconds - sched;
                lat.Add(ms);
                if (onDone != null) await onDone(gc, seq, clock.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) { Interlocked.Increment(ref errors); Console.Error.WriteLine(ex.Message); }
        }));
        issued++;
    }
    await Task.WhenAll(tasks);
    return (lat.ToArray(), total / clock.Elapsed.TotalSeconds, errors);
}

async Task<Dictionary<string, object>> GapCheck()
{
    var bad = await Scalar<long>(ds, @"SELECT count(*) FROM gc g, LATERAL (SELECT count(*) c, max(seq) m FROM gc_event e WHERE e.gc_id = g.gc_id) x
                                       WHERE x.c <> g.head_seq OR x.m <> g.head_seq");
    return new() { ["gcs_with_gap_or_head_mismatch"] = bad };
}

if (phases.Contains("load"))
{
    var listener = new Listener(Cs(dbName, "Application Name=s3-listener;"), ds, null);
    if (Arg("listen", "on") == "on") await listener.StartAsync();
    // warm-up
    await Paced(100, 3, PickGc);
    var (lat, ach, err) = await Paced(pacedRate, pacedSecs, PickGc);
    await Task.Delay(500);
    result["load"] = Stats(lat, new() { ["target_rate"] = pacedRate, ["achieved_rate"] = Math.Round(ach, 1), ["errors"] = err, ["notifications_received"] = listener.Received, ["hint_reads"] = listener.HintReads, ["insert_to_consumer_read_ms"] = Stats(listener.NotifyLatency.ToArray()) });
    if (Arg("listen", "on") == "on") await listener.StopAsync();
    // saturation: 32 closed-loop workers for 15 s
    var satLat = new ConcurrentBag<double>(); var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var satSw = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, 32).Select(w => Task.Run(async () =>
    {
        var r = new Random(w + run * 100);
        while (!stop.IsCancellationRequested)
        {
            var t0 = satSw.Elapsed.TotalMilliseconds;
            await Append(PickGc(r), w);
            satLat.Add(satSw.Elapsed.TotalMilliseconds - t0);
        }
    })));
    result["saturation_32w"] = Stats(satLat.ToArray(), new() { ["appends_per_s"] = Math.Round(satLat.Count / satSw.Elapsed.TotalSeconds, 0) });
    result["gap_check_after_load"] = await GapCheck();
    Console.WriteLine($"load: {JsonSerializer.Serialize(result["load"])}");
}

if (phases.Contains("hot"))
{
    // 200 simultaneous appends to one GC: gap-free seq, latency under contention
    long before = await Scalar<long>(ds, $"SELECT head_seq FROM gc WHERE gc_id='{hot}'");
    var go = new TaskCompletionSource();
    var hl = new ConcurrentBag<double>(); var seqs = new ConcurrentBag<long>();
    var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
    {
        await go.Task; var t = Stopwatch.StartNew();
        seqs.Add(await Append(hot, i)); hl.Add(t.Elapsed.TotalMilliseconds);
    })).ToArray();
    await Task.Delay(500); go.SetResult(); await Task.WhenAll(tasks);
    var s = seqs.OrderBy(x => x).ToArray();
    bool contiguous = s.Length == 200 && s[0] == before + 1 && s[^1] == before + 200 && s.Distinct().Count() == 200;
    result["hot_200_concurrent"] = Stats(hl.ToArray(), new() { ["gap_free"] = contiguous });
    Console.WriteLine($"hot: {JsonSerializer.Serialize(result["hot_200_concurrent"])}");
}

string pageSql = @"SELECT e.seq, e.event_id, e.kind, e.author_id, e.message_id, e.version, e.created_at, v.body_ct
  FROM gc_event e LEFT JOIN LATERAL (SELECT body_ct FROM gc_message_version v WHERE v.gc_id = e.gc_id AND v.message_id = e.message_id ORDER BY version DESC LIMIT 1) v ON true
  WHERE e.gc_id = $1 AND e.seq > $2 ORDER BY e.seq LIMIT $3";

async Task<(int rows, long bytes, long firstSeq, long lastSeq)> Page(Guid gc, long after, int n)
{
    await using var cmd = ds.CreateCommand(pageSql);
    cmd.Parameters.AddWithValue(gc); cmd.Parameters.AddWithValue(after); cmd.Parameters.AddWithValue(n);
    await using var rd = await cmd.ExecuteReaderAsync();
    int rows = 0; long bytes = 0, first = 0, last = 0;
    while (await rd.ReadAsync()) { rows++; last = rd.GetInt64(0); if (rows == 1) first = last; bytes += rd.IsDBNull(7) ? 0 : rd.GetFieldValue<byte[]>(7).Length; }
    return (rows, bytes, first, last);
}

if (phases.Contains("page"))
{
    long head = await Scalar<long>(ds, $"SELECT head_seq FROM gc WHERE gc_id='{hot}'");
    var r = new Random(run);
    var hotLat = new List<double>(); var coldLat = new List<double>(); var tailLat = new List<double>();
    for (int i = 0; i < 300; i++)
    {
        var t = Stopwatch.StartNew(); var p = await Page(hot, r.NextInt64(0, head - 200), 200); hotLat.Add(t.Elapsed.TotalMilliseconds);
        if (p.rows != 200) throw new Exception("short page");
        t.Restart(); await Page(GcId(1 + r.Next(GcCount - 1)), 0, 200); coldLat.Add(t.Elapsed.TotalMilliseconds);
        t.Restart(); await Page(hot, head - 200, 200); tailLat.Add(t.Elapsed.TotalMilliseconds);
    }
    result["page200_random_in_hot_100k"] = Stats(hotLat.ToArray());
    result["page200_first_page_of_901_gc"] = Stats(coldLat.ToArray());
    result["page200_latest_in_hot"] = Stats(tailLat.ToArray());
    Console.WriteLine($"page: hot-random p95={Pct(hotLat.ToArray(), .95):F2}ms");
}

if (phases.Contains("replay"))
{
    // 10k replay from the hot GC: 50 pages of 200, and one 10k statement; seq must be contiguous.
    var lat = new List<double>(); bool ok = true; long rowsTotal = 0;
    for (int rep = 0; rep < 5; rep++)
    {
        var t = Stopwatch.StartNew(); long after = 1000 + rep * 12000L, expect = after + 1;
        for (int p = 0; p < 50; p++)
        {
            await using var cmd = ds.CreateCommand(pageSql);
            cmd.Parameters.AddWithValue(hot); cmd.Parameters.AddWithValue(after); cmd.Parameters.AddWithValue(200);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) { long sq = rd.GetInt64(0); if (sq != expect++) ok = false; after = sq; rowsTotal++; }
        }
        lat.Add(t.Elapsed.TotalMilliseconds);
    }
    result["replay10k_paged_200"] = Stats(lat.ToArray(), new() { ["contiguous"] = ok, ["rows"] = rowsTotal });
    var one = new List<double>(); bool ok1 = true;
    for (int rep = 0; rep < 5; rep++)
    {
        var t = Stopwatch.StartNew(); var after = 1000 + rep * 12000L; var expect = after + 1; int n = 0;
        await using var cmd = ds.CreateCommand(pageSql);
        cmd.Parameters.AddWithValue(hot); cmd.Parameters.AddWithValue(after); cmd.Parameters.AddWithValue(10_000);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) { if (rd.GetInt64(0) != expect++) ok1 = false; n++; }
        if (n != 10_000) ok1 = false; one.Add(t.Elapsed.TotalMilliseconds);
    }
    result["replay10k_single_stmt"] = Stats(one.ToArray(), new() { ["contiguous"] = ok1 });
    Console.WriteLine($"replay: paged p50={Pct(lat.ToArray(), .5):F0}ms single p50={Pct(one.ToArray(), .5):F0}ms");
}

if (phases.Contains("index"))
{
    // gc_id + timestamp indexing, measured. "Events in this GC in the last hour" with and without (gc_id, created_at).
    var withoutLat = new List<double>(); string plan = "";
    string q = "SELECT count(*) FROM gc_event WHERE gc_id = $1 AND created_at > (SELECT max(created_at) FROM gc_event WHERE gc_id = $1) - interval '1 hour'";
    for (int i = 0; i < 20; i++)
    {
        var t = Stopwatch.StartNew(); await using var cmd = ds.CreateCommand(q); cmd.Parameters.AddWithValue(hot); await cmd.ExecuteScalarAsync(); withoutLat.Add(t.Elapsed.TotalMilliseconds);
    }
    await using (var cmd = ds.CreateCommand("EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) SELECT count(*) FROM gc_event WHERE gc_id = $1 AND created_at > now() - interval '1 hour'"))
    { cmd.Parameters.AddWithValue(hot); await using var rd = await cmd.ExecuteReaderAsync(); while (await rd.ReadAsync()) plan += rd.GetString(0) + "\n"; }
    result[tsIndex ? "ts_query_with_index" : "ts_query_pk_only"] = Stats(withoutLat.ToArray(), new() { ["plan"] = plan.Trim() });
    result["index_sizes_mb"] = await Scalar<long>(ds, "SELECT coalesce(sum(pg_relation_size(c.oid)),0)/1048576 FROM pg_class c WHERE c.relname ~ '^gc_event_p[0-9]+_(gc_id_created_at_idx|gc_id_.*idx)$'");
}

if (phases.Contains("reconnect"))
{
    // Writers at 300/s over 200 GCs; the listener backend is killed 4 times; the consumer catches up from its cursors on reconnect.
    var gcs = Enumerable.Range(1, 200).Select(GcId).ToArray();
    var clock = Stopwatch.StartNew();
    long startMark = await Scalar<long>(ds, "SELECT count(*) FROM gc_event");
    var startHeads = new Dictionary<Guid, long>();
    await using (var cmd = ds.CreateCommand("SELECT gc_id, head_seq FROM gc WHERE gc_id = ANY($1)"))
    { cmd.Parameters.AddWithValue(gcs); await using var rd = await cmd.ExecuteReaderAsync(); while (await rd.ReadAsync()) startHeads[rd.GetGuid(0)] = rd.GetInt64(1); }
    var listener = new Listener(Cs(dbName, "Application Name=s3-listener;"), ds, startHeads) { Clock = clock };
    await listener.StartAsync();
    clock.Restart(); listener.Clock = clock;
    var killer = Task.Run(async () =>
    {
        foreach (var t in new[] { 5, 12, 19, 26 })
        {
            var wait = TimeSpan.FromSeconds(t) - clock.Elapsed; if (wait > TimeSpan.Zero) await Task.Delay(wait);
            await Exec(ds, "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 's3-listener'");
            listener.KillTimes.Add(clock.Elapsed.TotalMilliseconds);
        }
    });
    var (lat, _, err) = await Paced(300, 32, r => gcs[r.Next(gcs.Length)], null);
    await killer;
    await Task.Delay(2000); // let the last catch-up finish
    await listener.StopAsync();
    // Ground truth: every event the writers committed vs. every (gc, seq) the consumer saw by either path.
    long lost = 0, dbEvents = 0, viaNotifyOnly = 0, viaCatchUpOnly = 0;
    await using (var cmd = ds.CreateCommand("SELECT gc_id, seq FROM gc_event WHERE gc_id = ANY($1) ORDER BY gc_id, seq"))
    {
        cmd.Parameters.AddWithValue(gcs); await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var key = (rd.GetGuid(0), rd.GetInt64(1));
            if (key.Item2 <= startHeads[key.Item1]) continue;
            dbEvents++;
            if (!listener.Seen.TryGetValue(key, out var seenPath)) lost++; else if (seenPath == 1) viaNotifyOnly++; else viaCatchUpOnly++;
        }
    }
    result["reconnect"] = new Dictionary<string, object>
    {
        ["events_written"] = dbEvents, ["lost_after_catchup"] = lost, ["read_after_hint"] = viaNotifyOnly, ["read_by_reconnect_catchup"] = viaCatchUpOnly,
        ["listener_reconnects"] = listener.Reconnects, ["reconnect_ms"] = listener.ReconnectMs.ToArray(),
        ["notifications_received"] = listener.Received, ["writer_errors"] = err,
        ["insert_to_consumer_read_ms"] = Stats(listener.NotifyLatency.ToArray()), ["append_latency_ms"] = Stats(lat),
    };
    Console.WriteLine($"reconnect: {JsonSerializer.Serialize(result["reconnect"])}");
}

notifierStop.Cancel(); await notifierTask;
Directory.CreateDirectory("results");
var path = $"results/{Arg("tag", "run")}{run}-p{parts}-rep{Arg("rep", "1")}-notify{notifyMode}-hot{hotShare}{(tsIndex ? "-ts" : "")}.json";
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote {path}");

// ---- helpers ----
static string Hex(string s) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
static async Task Exec(NpgsqlDataSource ds, string sql) { await using var c = ds.CreateCommand(sql); c.CommandTimeout = 600; await c.ExecuteNonQueryAsync(); }
static async Task<T> Scalar<T>(NpgsqlDataSource ds, string sql) { await using var c = ds.CreateCommand(sql); return (T)Convert.ChangeType((await c.ExecuteScalarAsync())!, typeof(T)); }
static double Pct(double[] v, double p) { if (v.Length == 0) return -1; var s = v.OrderBy(x => x).ToArray(); return s[Math.Min(s.Length - 1, (int)Math.Ceiling(p * s.Length) - 1)]; }
static Dictionary<string, object> Stats(double[] v, Dictionary<string, object>? extra = null)
{
    var d = new Dictionary<string, object> { ["n"] = v.Length, ["p50_ms"] = Math.Round(Pct(v, .5), 2), ["p95_ms"] = Math.Round(Pct(v, .95), 2), ["p99_ms"] = Math.Round(Pct(v, .99), 2), ["max_ms"] = Math.Round(v.Length == 0 ? 0 : v.Max(), 2) };
    if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
    return d;
}

// Consumer: LISTEN is only a wake hint. A hint (or a reconnect) queues a read of the log from the per-GC cursor; the log is the source of truth.
sealed class Listener(string cs, NpgsqlDataSource ds, Dictionary<Guid, long>? startHeads)
{
    public ConcurrentDictionary<(Guid, long), byte> Seen = new(); // 1 = read after a hint, 2 = read by reconnect catch-up
    public ConcurrentBag<double> NotifyLatency = new(), ReconnectMs = new(), KillTimes = new();
    public long Received, Reconnects, HintReads, CatchUpReads;
    readonly Dictionary<Guid, long> _cursor = new(startHeads ?? new());
    readonly System.Threading.Channels.Channel<Guid?> _work = System.Threading.Channels.Channel.CreateUnbounded<Guid?>(); // null = catch up everything
    readonly CancellationTokenSource _cts = new(); Task? _conn, _reader; double _lostAt; public Stopwatch? Clock;

    public async Task StartAsync()
    {
        if (startHeads == null)
            await using (var cmd = ds.CreateCommand("SELECT gc_id, head_seq FROM gc"))
            { await using var rd = await cmd.ExecuteReaderAsync(); while (await rd.ReadAsync()) _cursor[rd.GetGuid(0)] = rd.GetInt64(1); }
        var ready = new TaskCompletionSource(); _conn = Task.Run(() => Loop(ready)); _reader = Task.Run(Reader); await ready.Task;
    }
    public async Task StopAsync() { await Task.Delay(300); _cts.Cancel(); try { await _conn!; await _reader!; } catch (OperationCanceledException) { } }

    async Task Loop(TaskCompletionSource ready)
    {
        bool first = true;
        while (!_cts.IsCancellationRequested)
        {
            NpgsqlConnection? conn = null;
            try
            {
                conn = new NpgsqlConnection(cs); await conn.OpenAsync(_cts.Token);
                conn.Notification += (_, e) => OnNotify(e.Payload);
                await using (var c = new NpgsqlCommand("LISTEN gc", conn)) await c.ExecuteNonQueryAsync(_cts.Token);
                if (!first) { _work.Writer.TryWrite(null); ReconnectMs.Add((Clock?.Elapsed.TotalMilliseconds ?? 0) - _lostAt); Interlocked.Increment(ref Reconnects); }
                if (first) { first = false; ready.TrySetResult(); }
                while (!_cts.IsCancellationRequested) await conn.WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { _lostAt = Clock?.Elapsed.TotalMilliseconds ?? 0; await Task.Delay(50); }
            finally { if (conn != null) await conn.DisposeAsync(); }
        }
    }

    void OnNotify(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var gc = doc.RootElement.GetProperty("gc").GetGuid(); long seq = doc.RootElement.GetProperty("seq").GetInt64();
        Interlocked.Increment(ref Received);
        bool ahead; lock (_cursor) ahead = _cursor.TryGetValue(gc, out var cur) && seq > cur; // untracked GCs are not this consumer's
        if (ahead && Environment.GetEnvironmentVariable("S3_NOREAD") == null) _work.Writer.TryWrite(gc);
    }

    async Task Reader()
    {
        await foreach (var item in _work.Reader.ReadAllAsync(_cts.Token))
        {
            if (item is { } gc) { await ReadFrom(gc, 1); continue; }
            Guid[] gcs; lock (_cursor) gcs = _cursor.Keys.ToArray();
            var heads = new Dictionary<Guid, long>();
            await using (var cmd = ds.CreateCommand("SELECT gc_id, head_seq FROM gc WHERE gc_id = ANY($1)"))
            { cmd.Parameters.AddWithValue(gcs); await using var rd = await cmd.ExecuteReaderAsync(); while (await rd.ReadAsync()) heads[rd.GetGuid(0)] = rd.GetInt64(1); }
            foreach (var (g, head) in heads) { long cur; lock (_cursor) cur = _cursor[g]; if (head > cur) await ReadFrom(g, 2); }
        }
    }

    async Task ReadFrom(Guid gc, byte path)
    {
        long cur; lock (_cursor) cur = _cursor[gc];
        await using var cmd = ds.CreateCommand("SELECT seq, created_at FROM gc_event WHERE gc_id = $1 AND seq > $2 ORDER BY seq");
        cmd.Parameters.AddWithValue(gc); cmd.Parameters.AddWithValue(cur);
        await using var rd = await cmd.ExecuteReaderAsync();
        long last = cur;
        while (await rd.ReadAsync())
        {
            long s = rd.GetInt64(0); Seen[(gc, s)] = path; last = s;
            if (path == 1) { NotifyLatency.Add((DateTime.UtcNow - rd.GetDateTime(1).ToUniversalTime()).TotalMilliseconds); Interlocked.Increment(ref HintReads); }
            else Interlocked.Increment(ref CatchUpReads);
        }
        lock (_cursor) _cursor[gc] = Math.Max(_cursor[gc], last);
    }
}
