// Spike S2: Garnet on Windows through StackExchange.Redis. Modes: connect | write | verify.
// Password comes only from env S2_GARNET_PASSWORD; port from S2_GARNET_PORT (default 16379).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

const int N = 1000;
const int SurvivalTtlSeconds = 3600;
const int ShortTtlSeconds = 5;
const int LatencyIterations = 200;

string mode = args.Length > 0 ? args[0] : "";
string pw = Environment.GetEnvironmentVariable("S2_GARNET_PASSWORD") ?? throw new InvalidOperationException("S2_GARNET_PASSWORD not set");
string port = Environment.GetEnvironmentVariable("S2_GARNET_PORT") ?? "16379";
var opts = ConfigurationOptions.Parse($"127.0.0.1:{port},password={pw},abortConnect=false,connectTimeout=2000,syncTimeout=5000");
opts.AbortOnConnectFail = false;
var json = new JsonSerializerOptions { WriteIndented = true };

static string ValueFor(int i) => $"value-{i}-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("s2-" + i)));
static string Checksum(IEnumerable<(string k, string v)> kv)
{
    var sb = new StringBuilder();
    foreach (var (k, v) in kv.OrderBy(x => x.k, StringComparer.Ordinal)) sb.Append(k).Append('=').Append(v).Append('\n');
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
}
static double Median(List<double> xs) { var s = xs.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

async Task<ConnectionMultiplexer> ConnectAsync() => await ConnectionMultiplexer.ConnectAsync(opts);

if (mode == "connect")
{
    // Poll until Garnet answers; report ms since t0 (unix ms passed by the driver).
    long t0 = long.Parse(args[1]);
    var sw = Stopwatch.StartNew();
    string lastErr = "";
    while (sw.Elapsed < TimeSpan.FromSeconds(60))
    {
        try
        {
            using var c = await ConnectionMultiplexer.ConnectAsync(opts);
            if (c.IsConnected)
            {
                await c.GetDatabase().PingAsync();
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Console.WriteLine(JsonSerializer.Serialize(new { mode, restartToConnectMs = now - t0, serverVersion = c.GetServer("127.0.0.1:" + port).Version.ToString() }, json));
                return 0;
            }
        }
        catch (Exception e) { lastErr = e.GetType().Name + ": " + e.Message; }
        await Task.Delay(25);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { mode, error = "timeout", lastErr }));
    return 2;
}

// ---- shared command battery (step 5 and step 10) ----
async Task<List<object>> Battery(IDatabase db, string tag)
{
    var res = new List<object>();
    void Add(string cmd, string pattern, bool ok, string detail, double medianMs, double minMs, double maxMs)
        => res.Add(new { command = cmd, keyPattern = pattern, pass = ok, detail, medianMs = Math.Round(medianMs, 3), minMs = Math.Round(minMs, 3), maxMs = Math.Round(maxMs, 3) });

    // 5a SET with expiry (ICacheProvider.SetAsync: value + TTL)
    var lat = new List<double>(); bool ok = true;
    for (int i = 0; i < LatencyIterations; i++)
    {
        long t = Stopwatch.GetTimestamp();
        bool r = await db.StringSetAsync($"s2:{tag}:set:{i}", $"v{i}", TimeSpan.FromSeconds(60));
        lat.Add(Ms(Stopwatch.GetTimestamp() - t)); ok &= r;
    }
    Add("SET key value EX 60 (StringSetAsync with expiry)", $"s2:{tag}:set:{{i}}", ok, $"{LatencyIterations} ops", Median(lat), lat.Min(), lat.Max());

    // 5b GET existing + missing
    lat = []; ok = true;
    for (int i = 0; i < LatencyIterations; i++)
    {
        long t = Stopwatch.GetTimestamp();
        var v = await db.StringGetAsync($"s2:{tag}:set:{i}");
        lat.Add(Ms(Stopwatch.GetTimestamp() - t)); ok &= v.HasValue && (string?)v == $"v{i}";
    }
    Add("GET key (StringGetAsync)", $"s2:{tag}:set:{{i}}", ok, $"{LatencyIterations} ops, values equal", Median(lat), lat.Min(), lat.Max());
    lat = []; ok = true;
    for (int i = 0; i < LatencyIterations; i++)
    {
        long t = Stopwatch.GetTimestamp();
        var v = await db.StringGetAsync($"s2:{tag}:missing:{i}");
        lat.Add(Ms(Stopwatch.GetTimestamp() - t)); ok &= v.IsNull;
    }
    Add("GET missing key returns null", $"s2:{tag}:missing:{{i}}", ok, $"{LatencyIterations} ops, all IsNull", Median(lat), lat.Min(), lat.Max());

    // 5c projection snapshot: same SET/GET path, 64 KiB binary value, byte-compare
    var blob = new byte[64 * 1024]; RandomNumberGenerator.Fill(blob);
    lat = []; ok = true;
    for (int i = 0; i < 50; i++)
    {
        long t = Stopwatch.GetTimestamp();
        await db.StringSetAsync($"s2:{tag}:snap:{i}", blob, TimeSpan.FromSeconds(60));
        byte[]? back = await db.StringGetAsync($"s2:{tag}:snap:{i}");
        lat.Add(Ms(Stopwatch.GetTimestamp() - t)); ok &= back is not null && back.AsSpan().SequenceEqual(blob);
    }
    Add("SET+GET 64 KiB snapshot value (projection path)", $"s2:{tag}:snap:{{i}}", ok, "50 round trips, bytes equal", Median(lat), lat.Min(), lat.Max());

    // 5e expiry: short TTL reads as null afterwards
    await db.StringSetAsync($"s2:{tag}:exp", "x", TimeSpan.FromSeconds(ShortTtlSeconds));
    bool presentBefore = (await db.StringGetAsync($"s2:{tag}:exp")).HasValue;
    await Task.Delay(TimeSpan.FromSeconds(ShortTtlSeconds + 1));
    var after = await db.StringGetAsync($"s2:{tag}:exp");
    Add($"expiry: SET EX {ShortTtlSeconds}, GET after {ShortTtlSeconds + 1} s is null", $"s2:{tag}:exp", presentBefore && after.IsNull, $"presentBefore={presentBefore} afterIsNull={after.IsNull}", 0, 0, 0);
    return res;
}

// Separate finding (not a pass criterion): plain PUBLISH/SUBSCRIBE (step 5d, open item 4).
async Task<object> PubSub(ConnectionMultiplexer mux, string tag)
{
    var sub = mux.GetSubscriber(); var got = new List<(string ch, string msg)>();
    var tcs = new TaskCompletionSource();
    await sub.SubscribeAsync(RedisChannel.Literal($"s2:{tag}:chan"), (ch, msg) => { lock (got) { got.Add((ch!, msg!)); if (got.Count >= 10) tcs.TrySetResult(); } });
    var pubMux = await ConnectAsync(); var pub = pubMux.GetSubscriber();
    long receivers = 0; var lat = new List<double>();
    for (int i = 0; i < 10; i++)
    {
        long t = Stopwatch.GetTimestamp();
        receivers += await pub.PublishAsync(RedisChannel.Literal($"s2:{tag}:chan"), $"delta-{i}");
        lat.Add(Ms(Stopwatch.GetTimestamp() - t));
    }
    bool all = await Task.WhenAny(tcs.Task, Task.Delay(3000)) == tcs.Task;
    int patGot = 0;
    await sub.SubscribeAsync(RedisChannel.Pattern($"s2:{tag}:pat:*"), (_, _) => Interlocked.Increment(ref patGot));
    await pub.PublishAsync(RedisChannel.Literal($"s2:{tag}:pat:a"), "p");
    await Task.Delay(500);
    bool inOrder; lock (got) inOrder = got.Select(g => g.msg).SequenceEqual(Enumerable.Range(0, got.Count).Select(i => $"delta-{i}"));
    await sub.UnsubscribeAllAsync(); pubMux.Dispose();
    return new { command = "SUBSCRIBE/PUBLISH (+PSUBSCRIBE)", received = got.Count, of = 10, allReceived = all, inOrder, publishReturnSumReceivers = receivers, patternDelivered = patGot, publishMedianMs = Math.Round(Median(lat), 3), pass = all && inOrder && patGot == 1 };
}

if (mode == "write")
{
    var outp = new Dictionary<string, object?> { ["mode"] = mode, ["utc"] = DateTime.UtcNow };
    var sw = Stopwatch.StartNew();
    try
    {
        using var mux = await ConnectAsync();
        outp["step4_connect"] = new { ok = mux.IsConnected, ms = sw.ElapsedMilliseconds, endpoints = mux.GetEndPoints().Select(e => e.ToString()).ToArray() };
        var db = mux.GetDatabase();
        var server = mux.GetServer("127.0.0.1:" + port);
        outp["serverVersion"] = server.Version.ToString();
        outp["step5_battery"] = await Battery(db, "pre");
        outp["step5d_pubsub_separate_finding"] = await PubSub(mux, "pre");
        // step 6: survival set
        var kv = new List<(string, string)>();
        for (int i = 0; i < N; i++) { var k = $"s2:surv:{i:D4}"; var v = ValueFor(i); kv.Add((k, v)); await db.StringSetAsync(k, v, TimeSpan.FromSeconds(SurvivalTtlSeconds)); }
        // short-TTL set that must be expired after the restart
        for (int i = 0; i < 20; i++) await db.StringSetAsync($"s2:short:{i:D2}", "x", TimeSpan.FromSeconds(ShortTtlSeconds));
        outp["step6"] = new { N, ttlSeconds = SurvivalTtlSeconds, keyNames = "s2:surv:0000..s2:surv:0999", checksumSha256 = Checksum(kv), dbsize = (await db.ExecuteAsync("DBSIZE")).ToString() };
        await Task.Delay(1000);
    }
    catch (Exception e) { outp["error"] = e.ToString(); Console.WriteLine(JsonSerializer.Serialize(outp, json)); return 1; }
    Console.WriteLine(JsonSerializer.Serialize(outp, json));
    return 0;
}

if (mode == "verify")
{
    var outp = new Dictionary<string, object?> { ["mode"] = mode, ["utc"] = DateTime.UtcNow };
    try
    {
        using var mux = await ConnectAsync();
        var db = mux.GetDatabase();
        // step 9
        int match = 0, missing = 0, differ = 0; var kv = new List<(string, string)>(); var ttls = new List<double>();
        for (int i = 0; i < N; i++)
        {
            var k = $"s2:surv:{i:D4}"; var v = await db.StringGetAsync(k);
            if (v.IsNull) missing++; else if ((string?)v == ValueFor(i)) { match++; kv.Add((k, ValueFor(i))); } else differ++;
            if (!v.IsNull) { var t = await db.KeyTimeToLiveAsync(k); if (t is not null) ttls.Add(t.Value.TotalSeconds); }
        }
        int shortLeft = 0; for (int i = 0; i < 20; i++) if ((await db.StringGetAsync($"s2:short:{i:D2}")).HasValue) shortLeft++;
        outp["step9"] = new { N, match, missing, differ, checksumOfMatching = match == N ? Checksum(kv) : "n/a", ttlRemainingMinSec = ttls.Count > 0 ? Math.Round(ttls.Min()) : -1, ttlRemainingMaxSec = ttls.Count > 0 ? Math.Round(ttls.Max()) : -1, shortTtlKeysStillPresent = shortLeft, dbsize = (await db.ExecuteAsync("DBSIZE")).ToString() };
        // step 10
        outp["step10_battery"] = await Battery(db, "post");
        outp["step10_pubsub_separate_finding"] = await PubSub(mux, "post");
    }
    catch (Exception e) { outp["error"] = e.ToString(); Console.WriteLine(JsonSerializer.Serialize(outp, json)); return 1; }
    Console.WriteLine(JsonSerializer.Serialize(outp, json));
    return 0;
}
Console.Error.WriteLine("usage: connect <t0UnixMs> | write | verify");
return 64;
