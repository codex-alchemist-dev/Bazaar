using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.TalentPool.Http.Tests;

/// <summary>Real Kestrel on loopback against the real library routes.</summary>
public sealed class TalentLibraryRoutesTests : IAsyncLifetime
{
    private readonly InMemoryTalentTemplateStore _store = new();
    private readonly Hires _hires = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private int _guardCalls;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITalentTemplateStore>(_store);
        builder.Services.AddSingleton<IHireDecisionStore>(_hires);
        _app = builder.Build();
        _app.MapBazaarTalentLibrary(route => { _guardCalls++; return route; });
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private Task Add(int version, string prompt, string[] tools) =>
        _store.AddAsync(new TalentTemplate("reviewer", version, new SoulManifest("Reviewer", prompt, tools, ["anthropic:claude-haiku-5-5"], []), new string('a', 64), "a", DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken);

    private async Task Seed()
    {
        await Add(1, "one\ntwo", []);
        await Add(2, "one\nthree", ["shell"]);
        await Add(3, "one\nthree\nfour", ["shell"]);
        await _hires.SaveAsync(new HireVoteState("reviewer@1", Guid.NewGuid(), HireStatus.Released, MembershipDecision.Approved, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await _hires.SaveAsync(new HireVoteState("reviewer@2", Guid.NewGuid(), HireStatus.Blocked, MembershipDecision.Rejected, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
    }

    private async Task<JsonElement> Get(string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _http.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        return expected == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken) : default;
    }

    [Fact]
    public void Routes_are_mapped_through_the_guard() => Assert.Equal(3, _guardCalls);

    [Fact]
    public async Task The_library_lists_the_latest_version_with_its_standing()
    {
        await Seed();
        var entry = (await Get("/talent/templates"))[0];

        Assert.Equal("reviewer", entry.GetProperty("templateId").GetString());
        Assert.Equal(3, entry.GetProperty("latestVersion").GetInt32());
        Assert.Equal(3, entry.GetProperty("versionCount").GetInt32());
        Assert.Equal("quarantined", entry.GetProperty("standing").GetString());
        Assert.Equal(1, entry.GetProperty("toolCount").GetInt32());
    }

    [Fact]
    public async Task Versions_are_listed_oldest_first_with_each_versions_own_standing()
    {
        await Seed();
        var versions = await Get("/talent/templates/reviewer/versions");

        Assert.Equal(["released", "blocked", "quarantined"], versions.EnumerateArray().Select(v => v.GetProperty("standing").GetString()!).ToArray());
        Assert.Equal([1, 2, 3], versions.EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).ToArray());
        await Get("/talent/templates/nope/versions", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_diff_defaults_to_the_latest_against_the_one_before()
    {
        await Seed();
        var d = await Get("/talent/templates/reviewer/diff");

        Assert.Equal((2, 3), (d.GetProperty("fromVersion").GetInt32(), d.GetProperty("toVersion").GetInt32()));
        Assert.Equal(["same", "same", "added"], d.GetProperty("promptLines").EnumerateArray().Select(l => l.GetProperty("kind").GetString()!).ToArray());
        Assert.False(d.GetProperty("needsNewToolGrant").GetBoolean());
    }

    [Fact]
    public async Task An_explicit_diff_reports_added_tools_as_a_new_grant()
    {
        await Seed();
        var d = await Get("/talent/templates/reviewer/diff?from=1&to=2");

        Assert.Equal(["shell"], d.GetProperty("toolsAdded").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.True(d.GetProperty("needsNewToolGrant").GetBoolean());
        Assert.Equal(["same", "removed", "added"], d.GetProperty("promptLines").EnumerateArray().Select(l => l.GetProperty("kind").GetString()!).ToArray());
    }

    [Theory]
    [InlineData("?from=x", HttpStatusCode.BadRequest)]
    [InlineData("?from=0&to=2", HttpStatusCode.BadRequest)]
    [InlineData("?from=2&to=2", HttpStatusCode.BadRequest)]
    [InlineData("?from=1&to=9", HttpStatusCode.NotFound)]
    public async Task Bad_or_unknown_versions_are_refused(string query, HttpStatusCode expected)
    {
        await Seed();
        await Get("/talent/templates/reviewer/diff" + query, expected);
    }

    [Fact]
    public async Task A_single_version_template_has_nothing_to_diff_by_default()
    {
        await Add(1, "p", []);
        await Get("/talent/templates/reviewer/diff", HttpStatusCode.BadRequest);
        await Get("/talent/templates/nope/diff", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_clients_real_http_library_reads_what_the_routes_serve()
    {
        await Seed();
        var ct = TestContext.Current.CancellationToken;
        var client = Bazaar.Client.TalentLibrary.Http.HttpTalentLibraryClient.Create(new Bazaar.Client.Abstractions.Server.ServerEndpoint(new Uri(_app.Urls.First()), "tok"));

        var entry = Assert.Single(await client.ListAsync(ct));
        Assert.Equal(("reviewer", 3, "quarantined"), (entry.TemplateId, entry.LatestVersion, entry.Standing));
        Assert.Equal(3, (await client.VersionsAsync("reviewer", ct)).Count);
        var diff = await client.DiffAsync("reviewer", 1, 2, ct);
        Assert.True(diff!.NeedsNewToolGrant);
        Assert.Equal(["shell"], diff.ToolsAdded);
        Assert.Null(await client.DiffAsync("reviewer", 1, 9, ct));
        Assert.Empty(await client.VersionsAsync("nope", ct));
    }

    private sealed class Hires : IHireDecisionStore
    {
        private readonly Dictionary<string, HireVoteState> _s = [];
        public Task<HireVoteState?> FindAsync(string templateId, CancellationToken ct) => Task.FromResult(_s.GetValueOrDefault(templateId));
        public Task SaveAsync(HireVoteState state, CancellationToken ct) { _s[state.TemplateId] = state; return Task.CompletedTask; }
    }
}
