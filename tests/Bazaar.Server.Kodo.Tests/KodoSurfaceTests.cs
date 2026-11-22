using System.Net;
using System.Text.Json;
using Bazaar.Kodo.Anchoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Kodo.Tests;

public class KodoSurfaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<WebApplication> StartAsync(
        FakeReadings readings,
        FakeTimeProvider time,
        Dictionary<string, string?>? config = null,
        RecordingPublisher? publisher = null,
        Func<Microsoft.AspNetCore.Builder.RouteHandlerBuilder, Microsoft.AspNetCore.Builder.RouteHandlerBuilder>? guard = null,
        IKodoJournalStore? store = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<Bazaar.Kodo.Probes.Host.IHostReadings>(readings);
        builder.Services.AddSingleton<TimeProvider>(time);
        if (publisher is not null)
        {
            builder.Services.AddSingleton<IKodoStreamPublisher>(publisher);
        }

        if (store is not null)
        {
            builder.Services.AddSingleton(store);
        }

        builder.Services.AddBazaarKodo(new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build());
        var app = builder.Build();
        app.MapBazaarKodo(guard ?? (r => r));
        await app.StartAsync(Ct);
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.First()) };

    [Fact]
    public async Task Status_is_503_not_ready_then_200_with_the_snapshot()
    {
        var time = Make.Clock();
        // A tick interval far in the future still runs the first pass at start, so read before it by disabling the loop's access:
        // use a monitor whose pass has not run by asking a second app that never started its loop.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<Bazaar.Kodo.Probes.Host.IHostReadings>(new FakeReadings());
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddBazaarKodo();
        builder.Services.RemoveAll<IHostedService>();
        var cold = builder.Build();
        cold.MapBazaarKodo(r => r);
        await cold.StartAsync(Ct);
        using (var http = Client(cold))
        {
            using var res = await http.GetAsync("/api/kodo/v1/status", Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
            Assert.Equal("not_ready", doc.RootElement.GetProperty("code").GetString());
            Assert.True(doc.RootElement.GetProperty("retryable").GetBoolean());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("correlationId").GetString()));
        }

        await cold.DisposeAsync();

        await using var app = await StartAsync(new FakeReadings(), time);
        await app.Services.GetRequiredService<KodoMonitor>().RunCycleAsync(Ct);
        using var client = Client(app);
        using var ok = await client.GetAsync("/api/kodo/v1/status", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, body.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(4, body.RootElement.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task Events_lists_the_journal_newest_first_without_payload_or_paths()
    {
        await using var app = await StartAsync(new FakeReadings(), Make.Clock());
        await app.Services.GetRequiredService<KodoMonitor>().RunCycleAsync(Ct);
        using var http = Client(app);

        var text = await http.GetStringAsync("/api/kodo/v1/events?limit=3", Ct);

        using var doc = JsonDocument.Parse(text);
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(3, entries.GetArrayLength());
        Assert.True(entries[0].GetProperty("sequence").GetInt64() > entries[1].GetProperty("sequence").GetInt64());
        Assert.Equal(entries[0].GetProperty("hash").GetString(), doc.RootElement.GetProperty("head").GetString());
        Assert.DoesNotContain("payload", text);
        Assert.DoesNotContain(AppContext.BaseDirectory, text);
    }

    [Theory]
    [InlineData("/api/kodo/v1/events?limit=0")]
    [InlineData("/api/kodo/v1/events?limit=201")]
    [InlineData("/api/kodo/v1/events?limit=abc")]
    [InlineData("/api/kodo/v1/history?limit=0")]
    [InlineData("/api/kodo/v1/history?limit=201")]
    [InlineData("/api/kodo/v1/history?limit=abc")]
    [InlineData("/api/kodo/v1/history?cursor=abc")]
    [InlineData("/api/kodo/v1/history?cursor=-1")]
    [InlineData("/api/kodo/v1/history?at=2026-11-22T12:00:00Z")]
    [InlineData("/api/kodo/v1/journal/proof/abc")]
    public async Task Malformed_input_is_a_400_with_the_error_shape(string path)
    {
        await using var app = await StartAsync(new FakeReadings(), Make.Clock());
        using var http = Client(app);

        using var res = await http.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        Assert.False(doc.RootElement.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task History_reads_the_state_changes_from_the_store_newest_first_and_pages_by_cursor()
    {
        var store = new FakeJournalStore();
        for (int i = 1; i <= 5; i++)
        {
            store.StateChanges.Add(new KodoStateChange(i * 10, Make.Start.AddMinutes(i), "healthy", "degraded"));
        }

        await using var app = await StartAsync(new FakeReadings(), Make.Clock(), store: store);
        using var http = Client(app);

        using var first = JsonDocument.Parse(await http.GetStringAsync("/api/kodo/v1/history?limit=2", Ct));
        var entries = first.RootElement.GetProperty("entries");
        Assert.Equal([50L, 40L], entries.EnumerateArray().Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.Equal("healthy", entries[0].GetProperty("from").GetString());
        Assert.Equal("degraded", entries[0].GetProperty("to").GetString());
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;

        using var second = JsonDocument.Parse(await http.GetStringAsync($"/api/kodo/v1/history?limit=10&cursor={cursor}", Ct));
        Assert.Equal([30L, 20L, 10L], second.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.False(second.RootElement.TryGetProperty("nextCursor", out _));
        Assert.Equal(2, store.HistoryCalls);
    }

    [Fact]
    public async Task History_without_a_store_falls_back_to_the_state_records_in_memory_and_carries_no_payload_or_paths()
    {
        var readings = new FakeReadings();
        var time = Make.Clock();
        await using var app = await StartAsync(readings, time);
        var monitor = app.Services.GetRequiredService<KodoMonitor>();
        await monitor.RunCycleAsync(Ct);
        readings.DiskUsedPercent = 97;
        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);
        using var http = Client(app);

        var text = await http.GetStringAsync("/api/kodo/v1/history", Ct);

        using var doc = JsonDocument.Parse(text);
        var entries = doc.RootElement.GetProperty("entries");
        Assert.True(entries.GetArrayLength() >= 1);
        Assert.Equal("failing", entries[0].GetProperty("to").GetString());
        Assert.DoesNotContain("payload", text);
        Assert.DoesNotContain(AppContext.BaseDirectory, text);
    }

    [Fact]
    public async Task History_merges_records_the_store_has_not_received_yet_and_survives_a_database_outage()
    {
        var readings = new FakeReadings();
        var time = Make.Clock();
        var store = new FakeJournalStore();
        await using var app = await StartAsync(readings, time, store: store);
        var monitor = app.Services.GetRequiredService<KodoMonitor>();
        await monitor.RunCycleAsync(Ct);
        store.Down = true;
        readings.DiskUsedPercent = 97;
        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);
        using var http = Client(app);

        using var res = await http.GetAsync("/api/kodo/v1/history", Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        Assert.Equal("failing", doc.RootElement.GetProperty("entries")[0].GetProperty("to").GetString());
    }

    [Fact]
    public async Task Proof_verifies_against_the_returned_root_and_unknown_records_are_404()
    {
        await using var app = await StartAsync(new FakeReadings(), Make.Clock());
        await app.Services.GetRequiredService<KodoMonitor>().RunCycleAsync(Ct);
        using var http = Client(app);

        using var res = await http.GetAsync("/api/kodo/v1/journal/proof/1", Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var proof = doc.RootElement.GetProperty("proof");
        var merkle = new MerkleInclusionProof(
            proof.GetProperty("sequence").GetInt64(),
            proof.GetProperty("treeSize").GetInt64(),
            proof.GetProperty("path").EnumerateArray().Select(p => Convert.FromHexString(p.GetString()!)).ToArray());
        Assert.True(MerkleVerifier.VerifyInclusion(
            Convert.FromHexString(proof.GetProperty("recordHash").GetString()!),
            merkle,
            Convert.FromHexString(proof.GetProperty("root").GetString()!)));

        using var missing = await http.GetAsync("/api/kodo/v1/journal/proof/9999", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_proof_route_tells_a_pruned_record_and_a_damaged_checkpoint_apart_from_an_unknown_one()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IKodoStatusSource>(new StubSource());
        builder.Services.AddBazaarKodo(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Kodo:Enabled"] = "true" }).Build());
        builder.Services.RemoveAll<IHostedService>();
        await using var app = builder.Build();
        app.MapBazaarKodo(r => r);
        await app.StartAsync(Ct);
        using var http = Client(app);

        using var pruned = await http.GetAsync("/api/kodo/v1/journal/proof/1", Ct);
        using var damaged = await http.GetAsync("/api/kodo/v1/journal/proof/2", Ct);
        using var unknown = await http.GetAsync("/api/kodo/v1/journal/proof/3", Ct);
        using var anchors = await http.GetAsync("/api/kodo/v1/journal/anchors?limit=5", Ct);
        using var badLimit = await http.GetAsync("/api/kodo/v1/journal/anchors?limit=0", Ct);

        Assert.Equal(HttpStatusCode.Gone, pruned.StatusCode);
        Assert.Contains("record_pruned", await pruned.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Conflict, damaged.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, anchors.StatusCode);
        using var doc = JsonDocument.Parse(await anchors.Content.ReadAsStringAsync(Ct));
        var checkpoint = Assert.Single(doc.RootElement.GetProperty("checkpoints").EnumerateArray());
        Assert.Equal(7, checkpoint.GetProperty("lastSequence").GetInt64());
        Assert.Equal("file", checkpoint.GetProperty("anchors")[0].GetProperty("sinkId").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
    }

    [Fact]
    public async Task The_checkpoint_loop_is_off_by_default_and_cuts_and_anchors_through_the_host_when_configured()
    {
        await using (var off = await StartAsync(new FakeReadings(), Make.Clock()))
        {
            Assert.DoesNotContain(off.Services.GetServices<IHostedService>(), s => s.GetType().Name == "KodoCheckpointService");
        }

        var time = Make.Clock();
        var journal = new FakeJournalStore();
        var checkpoints = new FakeCheckpointStore(journal);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<Bazaar.Kodo.Probes.Host.IHostReadings>(new FakeReadings());
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<IKodoJournalStore>(journal);
        builder.Services.AddSingleton<IKodoCheckpointStore>(checkpoints);
        string file = Path.Combine(Path.GetTempPath(), "kodo-anchor-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            builder.Services.AddBazaarKodo(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Kodo:CheckpointsEnabled"] = "true",
                ["Bazaar:Kodo:CheckpointMaxRecords"] = "1",
                ["Bazaar:Kodo:AnchorFilePath"] = file,
            }).Build());
            await using var app = builder.Build();
            await app.StartAsync(Ct);

            await WaitAsync(() => journal.Records.Count > 0);
            await WaitAsync(() =>
            {
                time.Advance(TimeSpan.FromSeconds(5));
                return checkpoints.Anchors.Count > 0;
            });

            Assert.Contains(app.Services.GetServices<IHostedService>(), s => s.GetType().Name == "KodoCheckpointService");
            Assert.Equal("file", checkpoints.Anchors[0].Sink);
            Assert.NotEmpty(await File.ReadAllLinesAsync(file, Ct));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private sealed class StubSource : IKodoStatusSource
    {
        public bool Enabled => true;

        public Bazaar.Contracts.Kodo.KodoStatusSnapshotDto? Latest => null;

        public IReadOnlyList<KodoJournalEntryView> RecentJournal(int limit) => [];

        public KodoJournalProofView? JournalProof(long sequence) => null;

        public ValueTask<KodoProofLookup> LookupJournalProofAsync(long sequence, CancellationToken cancellationToken) =>
            ValueTask.FromResult(sequence switch
            {
                1 => new KodoProofLookup(KodoProofOutcome.Pruned),
                2 => new KodoProofLookup(KodoProofOutcome.Inconsistent),
                _ => new KodoProofLookup(KodoProofOutcome.NotAvailable),
            });

        public ValueTask<IReadOnlyList<KodoCheckpointView>> JournalAnchorsAsync(int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<KodoCheckpointView>>([new KodoCheckpointView(0, 7, 8, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "aa", "bb", "cc", true, false, null, [new KodoAnchorMark("file", DateTimeOffset.UnixEpoch)])]);
    }

    [Fact]
    public async Task Every_route_goes_through_the_host_guard()
    {
        await using var app = await StartAsync(
            new FakeReadings(),
            Make.Clock(),
            guard: r => r.AddEndpointFilter(async (ctx, next) =>
                ctx.HttpContext.Request.Headers.ContainsKey("X-Test-Auth") ? await next(ctx) : Microsoft.AspNetCore.Http.Results.StatusCode(401)));
        using var http = Client(app);

        foreach (var path in new[] { "/api/kodo/v1/status", "/api/kodo/v1/events", "/api/kodo/v1/history", "/api/kodo/v1/journal/proof/0", "/api/kodo/v1/journal/anchors" })
        {
            using var res = await http.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task A_switched_off_module_runs_nothing_and_answers_404()
    {
        var readings = new FakeReadings();
        var publisher = new RecordingPublisher();
        var time = Make.Clock();
        await using var app = await StartAsync(readings, time, new() { ["Bazaar:Kodo:Enabled"] = "false" }, publisher);

        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50, Ct);

        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s.GetType().Name == "KodoMonitorService");
        Assert.False(app.Services.GetRequiredService<IKodoStatusSource>().Enabled);
        Assert.Equal(0, readings.DiskReads);
        Assert.Equal(0, readings.MemoryReads);
        Assert.Empty(publisher.Events);
        using var http = Client(app);
        foreach (var path in new[] { "/api/kodo/v1/status", "/api/kodo/v1/events", "/api/kodo/v1/history", "/api/kodo/v1/journal/proof/0" })
        {
            using var res = await http.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Contains("kodo_disabled", await res.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task The_hosted_loop_runs_at_start_and_on_each_tick_of_the_fake_clock()
    {
        var readings = new FakeReadings();
        var publisher = new RecordingPublisher();
        var time = Make.Clock();
        await using var app = await StartAsync(readings, time, new() { ["Bazaar:Kodo:TickSeconds"] = "10" }, publisher);
        var source = app.Services.GetRequiredService<IKodoStatusSource>();

        await WaitAsync(() => source.Latest is not null);
        Assert.Equal(1, readings.DiskReads);

        time.Advance(TimeSpan.FromSeconds(10));
        await WaitAsync(() => publisher.Events.Count >= 2);
        Assert.Contains(publisher.Events, e => e.Type == "heartbeat");
        Assert.Equal(1, readings.DiskReads);

        time.Advance(TimeSpan.FromMinutes(1));
        await WaitAsync(() => readings.DiskReads >= 2);
    }

    [Fact]
    public async Task A_loop_whose_readings_all_throw_keeps_running_and_stops_cleanly()
    {
        var readings = new FakeReadings { Throw = true };
        var time = Make.Clock();
        var app = await StartAsync(readings, time, new() { ["Bazaar:Kodo:TickSeconds"] = "10" });
        var source = app.Services.GetRequiredService<IKodoStatusSource>();
        await WaitAsync(() => source.Latest is not null);

        time.Advance(TimeSpan.FromMinutes(2));
        await WaitAsync(() => readings.DiskReads >= 2);

        await app.StopAsync(Ct);
        await app.DisposeAsync();
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(10, Ct);
        }
    }
}
