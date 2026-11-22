using System.Net;
using System.Text.Json;
using Bazaar.Server.Git.Reads;
using Bazaar.Server.Git.RepositoryDirectory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Git.Tests.Reads;

/// <summary>Real Kestrel on a loopback port: the <c>GET /git/repos</c> route over a configured directory.</summary>
public sealed class GitRepoListRouteTests
{
    private const string _secretPath = "/srv/secret-clones/widgets";

    private static GitRepositoryEntry Entry(string id, string provider, string name, string? installation = null) => new()
    {
        RepositoryId = id,
        Provider = provider,
        FullName = name,
        LocalClonePath = _secretPath,
        InstallationId = installation,
        ProviderRepositoryId = "node-" + id,
    };

    private static async Task<(HttpStatusCode Status, string Text)> Get(params GitRepositoryEntry[] entries)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddBazaarGit();
        builder.Services.Replace(ServiceDescriptor.Singleton<IGitRepositoryDirectory>(new ConfiguredGitRepositoryDirectory(new GitRepositoryDirectoryOptions { Repositories = [.. entries] })));

        await using var app = builder.Build();
        var guarded = 0;
        app.MapBazaarGitReads(route =>
        {
            guarded++;
            return route;
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var response = await http.GetAsync("/git/repos", TestContext.Current.CancellationToken);
        Assert.True(guarded > 0);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Empty_directory_returns_an_empty_array()
    {
        var (status, text) = await Get();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("[]", text);
    }

    [Fact]
    public async Task Lists_repos_ordered_by_provider_then_full_name()
    {
        var (status, text) = await Get(
            Entry("c", "github", "zed/last"),
            Entry("b", "gitlab", "a/first"),
            Entry("a", "github", "acme/widgets"));

        Assert.Equal(HttpStatusCode.OK, status);
        var items = JsonElement.Parse(text).EnumerateArray().Select(e => $"{e.GetProperty("provider").GetString()}:{e.GetProperty("fullName").GetString()}").ToArray();
        Assert.Equal(["github:acme/widgets", "github:zed/last", "gitlab:a/first"], items);
    }

    [Fact]
    public async Task Never_leaks_the_clone_path_installation_id_or_provider_repo_id()
    {
        var (_, text) = await Get(Entry("a", "github", "acme/widgets", installation: "inst-424242"));

        Assert.DoesNotContain("secret-clones", text, StringComparison.Ordinal);
        Assert.DoesNotContain("inst-424242", text, StringComparison.Ordinal);
        Assert.DoesNotContain("node-a", text, StringComparison.Ordinal);
        var only = JsonElement.Parse(text)[0];
        Assert.Equal(["provider", "fullName"], only.EnumerateObject().Select(p => p.Name).ToArray());
    }
}
