using System.Net;
using System.Text;
using Bazaar.Testing.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Health.Tests;

public class HealthReportFactSourceTests
{
    private static readonly DateOnly _day = new(2026, 11, 22);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reports_become_coverage_analyzer_and_package_audit_facts()
    {
        var store = new InMemoryHealthReportStore();
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _day, "run-1", "<coverage/>"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.Analyzer, "dotnet", _day, "run-1", "{}"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.PackageAudit, "dotnet", _day, "run-1", "{\"projects\":[]}"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _day.AddDays(-1), "old", "<old/>"), Ct);
        await store.PutAsync(new("other", HealthReportKinds.Coverage, "dotnet", _day, "x", "<x/>"), Ct);

        var facts = await new HealthReportFactSource(store).ReadAsync("p", _day, Ct);

        Assert.Equal("<coverage/>", Assert.Single(facts.Coverage).CoberturaXml);
        Assert.Equal("dotnet", Assert.Single(facts.Analyzer).Toolchain);
        Assert.Equal("run-1", facts.PackageAudit!.SourceRef);
    }

    [Fact]
    public async Task A_day_without_reports_is_a_gap()
    {
        var facts = await new HealthReportFactSource(new InMemoryHealthReportStore()).ReadAsync("p", _day, Ct);

        Assert.Empty(facts.Coverage);
        Assert.Empty(facts.Analyzer);
        Assert.Null(facts.PackageAudit);
    }

    [Fact]
    public async Task The_same_key_replaces_the_report_and_prune_removes_old_days()
    {
        var store = new InMemoryHealthReportStore();
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _day, "r", "v1"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _day, "r", "v2"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _day.AddDays(-100), "r", "old"), Ct);

        Assert.Equal("v2", Assert.Single(await store.ListAsync("p", _day, Ct)).Content);
        Assert.Equal(1, await store.PruneBeforeAsync(_day.AddDays(-90), Ct));
        Assert.Empty(await store.ListAsync("p", _day.AddDays(-100), Ct));
        Assert.Equal(["p"], await new HealthReportFactSource(store).ListProjectsAsync(Ct));
    }
}

public sealed class PgHealthReportStoreTests(HealthDb db) : IClassFixture<HealthDb>
{
    private static readonly DateOnly _day = new(2026, 11, 22);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<PgHealthReportStore> FreshAsync()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var cmd = db.DataSource.CreateCommand("TRUNCATE health_reports");
        await cmd.ExecuteNonQueryAsync(Ct);
        return new PgHealthReportStore(db.DataSource);
    }

    [Fact]
    public async Task Put_replaces_the_same_key_and_list_reads_one_project_and_day()
    {
        var store = await FreshAsync();
        await store.PutAsync(new("p", HealthReportKinds.Analyzer, "dotnet", _day, "r", "v1"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.Analyzer, "dotnet", _day, "r", "v2"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _day, "r", "<c/>"), Ct);
        await store.PutAsync(new("q", HealthReportKinds.Coverage, "dotnet", _day, "r", "<q/>"), Ct);

        var list = await store.ListAsync("p", _day, Ct);

        Assert.Equal([HealthReportKinds.Analyzer, HealthReportKinds.Coverage], list.Select(r => r.Kind));
        Assert.Equal("v2", list[0].Content);
        Assert.Equal(["p", "q"], await store.ListProjectsAsync(Ct));
    }

    [Fact]
    public async Task Prune_removes_only_days_before_the_cutoff()
    {
        var store = await FreshAsync();
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _day.AddDays(-91), "a", "old"), Ct);
        await store.PutAsync(new("p", HealthReportKinds.Coverage, "dotnet", _day.AddDays(-90), "b", "edge"), Ct);

        Assert.Equal(1, await store.PruneBeforeAsync(_day.AddDays(-90), Ct));
        Assert.Equal("edge", Assert.Single(await store.ListAsync("p", _day.AddDays(-90), Ct)).Content);
    }

    [Fact]
    public async Task An_unknown_kind_is_refused_by_the_table()
    {
        var store = await FreshAsync();
        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() =>
            store.PutAsync(new("p", "bogus", "dotnet", _day, "r", "x"), Ct));
    }
}

public class HealthReportEndpointTests
{
    private static async Task<(WebApplication App, InMemoryHealthReportStore Store)> StartAsync()
    {
        var store = new InMemoryHealthReportStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IHealthReportStore>(store);
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero)));
        var app = builder.Build();
        app.MapBazaarProjectHealthReports(r => r);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, store);
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app, string project, string json)
    {
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return await http.PostAsync($"/projects/{project}/health/reports", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_valid_push_is_stored_for_today_by_default()
    {
        var (app, store) = await StartAsync();
        await using var _ = app;

        using var res = await PostAsync(app, "alpha", """{"kind":"coverage","toolchain":"dotnet","source_ref":"run-9","content":"<coverage/>"}""");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var stored = Assert.Single(await store.ListAsync("alpha", new DateOnly(2026, 11, 22), TestContext.Current.CancellationToken));
        Assert.Equal("run-9", stored.SourceRef);
    }

    [Theory]
    [InlineData("""{"kind":"bogus","toolchain":"dotnet","source_ref":"r","content":"x"}""")]
    [InlineData("""{"kind":"analyzer","toolchain":"","source_ref":"r","content":"x"}""")]
    [InlineData("""{"kind":"analyzer","toolchain":"dotnet","source_ref":"r","content":""}""")]
    [InlineData("""{"kind":"analyzer","toolchain":"dotnet","day":"22/11/2026","source_ref":"r","content":"x"}""")]
    [InlineData("not json")]
    public async Task A_bad_body_is_a_400(string json)
    {
        var (app, store) = await StartAsync();
        await using var _ = app;

        using var res = await PostAsync(app, "alpha", json);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(await store.ListProjectsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_malformed_project_id_is_a_400()
    {
        var (app, _) = await StartAsync();
        await using var _a = app;

        using var res = await PostAsync(app, ".hidden", """{"kind":"coverage","toolchain":"dotnet","source_ref":"r","content":"x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("bad_project_id", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
