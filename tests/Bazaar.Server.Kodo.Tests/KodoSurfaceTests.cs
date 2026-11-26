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
        Func<Microsoft.AspNetCore.Builder.RouteHandlerBuilder, Microsoft.AspNetCore.Builder.RouteHandlerBuilder>? guard = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<Bazaar.Kodo.Probes.Host.IHostReadings>(readings);
        builder.Services.AddSingleton<TimeProvider>(time);
        if (publisher is not null)
        {
            builder.Services.AddSingleton<IKodoStreamPublisher>(publisher);
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
    public async Task Every_route_goes_through_the_host_guard()
    {
        await using var app = await StartAsync(
            new FakeReadings(),
            Make.Clock(),
            guard: r => r.AddEndpointFilter(async (ctx, next) =>
                ctx.HttpContext.Request.Headers.ContainsKey("X-Test-Auth") ? await next(ctx) : Microsoft.AspNetCore.Http.Results.StatusCode(401)));
        using var http = Client(app);

        foreach (var path in new[] { "/api/kodo/v1/status", "/api/kodo/v1/events", "/api/kodo/v1/journal/proof/0" })
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
        foreach (var path in new[] { "/api/kodo/v1/status", "/api/kodo/v1/events", "/api/kodo/v1/journal/proof/0" })
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
