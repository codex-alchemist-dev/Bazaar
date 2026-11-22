using System.Reflection;
using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.Knowledge.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Health.Tests;

public class HealthSurfaceTests
{
    private static readonly DateOnly _today = new(2026, 11, 22);

    private static async Task<WebApplication> StartAsync(InMemoryMetricStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IMetricStore>(store);
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero)));
        builder.Services.AddBazaarHealth();
        var app = builder.Build();
        app.MapBazaarProjectHealth(r => r);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static string BaseUrl(WebApplication app) => app.Urls.First();

    [Fact]
    public async Task Endpoint_returns_the_report_as_snake_case_json()
    {
        var store = new InMemoryMetricStore();
        await store.UpsertAsync([
            new("alpha", MetricNames.CoverageLinePercent, "dotnet", _today.AddDays(-1), 80, "a"),
            new("alpha", MetricNames.CoverageLinePercent, "dotnet", _today, 70, "b"),
            new("alpha", HealthMetricNames.Reverts, "", _today, 2, "c"),
        ], TestContext.Current.CancellationToken);
        await using var app = await StartAsync(store);
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl(app)) };

        using var res = await http.GetAsync("/projects/alpha/health", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        Assert.Equal("alpha", root.GetProperty("project_id").GetString());
        Assert.Equal("Alert", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("tech_debt").GetProperty("reverts_in_window").GetDouble());
        Assert.Equal("dotnet", root.GetProperty("coverage")[0].GetProperty("toolchain").GetString());
    }

    [Fact]
    public async Task Endpoint_rejects_a_malformed_project_id()
    {
        await using var app = await StartAsync(new InMemoryMetricStore());
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl(app)) };

        using var res = await http.GetAsync("/projects/.hidden/health", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("bad_project_id", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Endpoint_applies_the_host_guard()
    {
        var store = new InMemoryMetricStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IMetricStore>(store);
        builder.Services.AddBazaarHealth();
        await using var app = builder.Build();
        app.MapBazaarProjectHealth(r => r.AddEndpointFilter(async (ctx, next) => Results.StatusCode(401)));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl(app)) };

        using var res = await http.GetAsync("/projects/alpha/health", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Dashboard_card_has_dashboard_pane_shape_and_says_no_data_instead_of_zero()
    {
        var svc = new ProjectHealthService(new InMemoryMetricStore(), new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero)));
        var card = HealthDashboardCard.Build(await svc.GetAsync("alpha", TestContext.Current.CancellationToken));

        Assert.Equal("Project health: alpha", card.Title);
        Assert.Equal("NoData", card.Items.Single(i => i.Label == "Status").Value);
        Assert.Equal(HealthDashboardCard.NoData, card.Items.Single(i => i.Label == "Build pass rate").Value);
        Assert.Equal(HealthDashboardCard.NoData, card.Items.Single(i => i.Label == "Reverts").Value);

        using var doc = JsonDocument.Parse(HealthDashboardCard.ToPaneModel(card));
        var section = doc.RootElement.GetProperty("sections")[0];
        Assert.Equal(card.Title, section.GetProperty("title").GetString());
        Assert.Equal("Status", section.GetProperty("items")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Dashboard_card_shows_alerts_trends_and_pass_rate()
    {
        var store = new InMemoryMetricStore();
        await store.UpsertAsync([
            new("alpha", MetricNames.CoverageLinePercent, "dotnet", _today.AddDays(-1), 80, "a"),
            new("alpha", MetricNames.CoverageLinePercent, "dotnet", _today, 70, "b"),
            new("alpha", MetricNames.CiRunsTotal, "", _today, 4, "c"),
            new("alpha", MetricNames.CiRunsPassed, "", _today, 3, "c"),
            new("alpha", HealthMetricNames.BlockedShare, "", _today, 0.25, "d"),
        ], TestContext.Current.CancellationToken);
        var svc = new ProjectHealthService(store, new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero)));
        var card = HealthDashboardCard.Build(await svc.GetAsync("alpha", TestContext.Current.CancellationToken));

        Assert.Equal("Alert", card.Items.Single(i => i.Label == "Status").Value);
        Assert.Contains(card.Items, i => i.Label == "Alert");
        Assert.Equal("70% (-10)", card.Items.Single(i => i.Label == "Coverage (dotnet)").Value);
        Assert.Equal("75% (3 of 4)", card.Items.Single(i => i.Label == "Build pass rate").Value);
        Assert.Equal("0.25", card.Items.Single(i => i.Label == "Blocked share").Value);
    }

    [Fact]
    public void Health_cannot_reach_reputation_scheduling_or_voting_code()
    {
        // doc 25 Boundaries: per-agent and morale figures have no write path into reputation, the Work-Life schedule or votes.
        string[] forbidden = ["Reputation", "WorkLife", "Council", "Souls"];
        foreach (var assembly in new[] { typeof(ProjectHealthService).Assembly, typeof(IProjectHealthService).Assembly })
        {
            var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();
            foreach (var name in referenced)
                Assert.DoesNotContain(forbidden, f => name.Contains(f, StringComparison.Ordinal));
            Assert.DoesNotContain("Bazaar.Server", referenced);
        }
    }

    [Fact]
    public void Component_manifest_and_defaults_match_doc_25()
    {
        Assert.Equal("health", HealthServerComponent.ManifestDefinition.Id);
        Assert.Equal(ComponentKind.Feature, HealthServerComponent.ManifestDefinition.Kind);

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Health:CoverageDropPoints"] = "5",
            ["Bazaar:Health:MoraleSignals:0"] = "reopened_tasks",
        }).Build();
        var services = new ServiceCollection().AddBazaarHealth(cfg);
        using var sp = services.BuildServiceProvider();
        var s = sp.GetRequiredService<ProjectHealthSettings>();

        Assert.Equal(5, s.CoverageDropPoints);
        Assert.Equal(3, s.BuildFailureStreak);
        Assert.Equal(["reopened_tasks"], s.MoraleSignals);
        Assert.Equal(2, ProjectHealthSettings.Defaults.CoverageDropPoints);
    }
}
