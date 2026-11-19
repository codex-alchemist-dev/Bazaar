using System.Net;
using System.Text.Json;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;
using Bazaar.Server.GitSolarSystem.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using static Bazaar.Server.GitSolarSystem.Tests.Hist;

namespace Bazaar.Server.GitSolarSystem.Tests;

/// <summary>Route tests on a real listener bound to loopback only (no outside network).</summary>
public sealed class GitSolarRoutesTests
{
    private static ValueTask<IResult?> Allow(HttpContext _) => ValueTask.FromResult<IResult?>(null);

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        FakeHistorySource history,
        Func<HttpContext, ValueTask<IResult?>> guard,
        FixedSettings? settings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IGitSolarHistorySource>(history);
        builder.Services.AddSingleton<IGitSolarLayout>(new FakeLayout());
        builder.Services.AddSingleton<IGitSolarSettings>(settings ?? new FixedSettings());
        builder.Services.AddBazaarGitSolarSystem();

        var app = builder.Build();
        GitSolarRoutes.Map(app, guard);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static FakeHistorySource Repo() => new FakeHistorySource().With("acme", "app", Linear(3));

    [Fact]
    public async Task Known_repository_answers_200_with_a_feed_that_round_trips_through_the_wire_context()
    {
        var (app, client) = await StartAsync(Repo(), Allow);
        await using var _ = app;
        using var __ = client;

        var response = await client.GetAsync("/git/github/repos/acme/app/solar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains(GitSolarRoutes.TruncatedHeader));
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var feed = JsonSerializer.Deserialize(json, WireJsonContext.Default.GitSolarFeed);
        Assert.NotNull(feed);
        Assert.Equal(3, feed.Snapshot.Commits.Count);
        Assert.Equal(9, feed.Frame.Xyz.Length);
        Assert.Equal(feed.Snapshot.GraphVersion, feed.Frame.GraphVersion);
    }

    [Fact]
    public async Task Unknown_repository_answers_404()
    {
        var (app, client) = await StartAsync(Repo(), Allow);
        await using var _ = app;
        using var __ = client;

        var response = await client.GetAsync("/git/github/repos/acme/missing/solar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Provider_with_unsupported_characters_answers_400()
    {
        var (app, client) = await StartAsync(Repo(), Allow);
        await using var _ = app;
        using var __ = client;

        var response = await client.GetAsync("/git/bad.provider/repos/acme/app/solar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Guard_refusal_is_returned_as_is_and_the_history_is_never_read()
    {
        var history = Repo();
        var refusal = Results.StatusCode(StatusCodes.Status403Forbidden);
        var (app, client) = await StartAsync(history, _ => ValueTask.FromResult<IResult?>(refusal));
        await using var _ = app;
        using var __ = client;

        var response = await client.GetAsync("/git/github/repos/acme/app/solar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, history.Reads);
    }

    [Fact]
    public async Task Guard_that_allows_lets_the_request_through()
    {
        var history = Repo();
        var (app, client) = await StartAsync(history, Allow);
        await using var _ = app;
        using var __ = client;

        var response = await client.GetAsync("/git/github/repos/acme/app/solar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, history.Reads);
    }

    [Fact]
    public async Task History_over_the_commit_cap_answers_200_with_the_newest_commits_and_the_truncated_header()
    {
        var history = new FakeHistorySource().With("acme", "app", Linear(6));
        var (app, client) = await StartAsync(history, Allow, new FixedSettings { MaxCommits = 2 });
        await using var _ = app;
        using var __ = client;

        var response = await client.GetAsync("/git/github/repos/acme/app/solar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", Assert.Single(response.Headers.GetValues(GitSolarRoutes.TruncatedHeader)));
        var feed = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), WireJsonContext.Default.GitSolarFeed);
        Assert.Equal(["c4", "c5"], feed!.Snapshot.Commits.Select(c => c.Sha));
    }
}
