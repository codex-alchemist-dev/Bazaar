using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Community.GitHub.Bridge.Tests;

public sealed class ConfiguredResolverTests
{
    private static ConfiguredCommunityInstallationResolver Make(params CommunityInstallationEntry[] entries) =>
        new(Options.Create(new CommunityInstallationOptions { Entries = [.. entries] }));

    private static CommunityInstallationEntry E(string owner, string repo, long inst = 10, long repoId = 20) =>
        new() { Owner = owner, Repo = repo, InstallationId = inst, RepositoryId = repoId };

    [Fact]
    public async Task Exact_match_returns_ids()
    {
        var r = await Make(E("acme", "widgets", 7, 8)).ResolveAsync("acme", "widgets", CancellationToken.None);
        Assert.Equal(new CommunityInstallation(7, 8), r);
    }

    [Fact]
    public async Task Match_is_case_insensitive()
    {
        var r = await Make(E("Acme", "Widgets", 7, 8)).ResolveAsync("ACME", "widgets", CancellationToken.None);
        Assert.Equal(new CommunityInstallation(7, 8), r);
    }

    [Fact]
    public async Task Wildcard_covers_owner_but_exact_wins()
    {
        var resolver = Make(E("acme", "*", 1, 2), E("acme", "special", 3, 4));
        Assert.Equal(new CommunityInstallation(1, 2), await resolver.ResolveAsync("acme", "other", CancellationToken.None));
        Assert.Equal(new CommunityInstallation(3, 4), await resolver.ResolveAsync("acme", "special", CancellationToken.None));
    }

    [Fact]
    public async Task Wildcard_does_not_cross_owners()
    {
        Assert.Null(await Make(E("acme", "*")).ResolveAsync("evil", "widgets", CancellationToken.None));
    }

    [Theory]
    [InlineData("acme", "unlisted")]
    [InlineData("other", "widgets")]
    [InlineData("", "widgets")]
    [InlineData("acme", " ")]
    public async Task Unlisted_returns_null(string owner, string repo)
    {
        Assert.Null(await Make(E("acme", "widgets")).ResolveAsync(owner, repo, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_allowlist_fails_closed()
    {
        Assert.Null(await Make().ResolveAsync("acme", "widgets", CancellationToken.None));
    }

    [Theory]
    [InlineData("", "r", 1, 1)]
    [InlineData("*", "r", 1, 1)]
    [InlineData("o", "", 1, 1)]
    [InlineData("o", "a/b", 1, 1)]
    [InlineData("o", "r", 0, 1)]
    [InlineData("o", "r", 1, -5)]
    public void Invalid_entries_are_rejected(string owner, string repo, long inst, long repoId)
    {
        var result = new CommunityInstallationOptionsValidator().Validate(null, new CommunityInstallationOptions { Entries = [E(owner, repo, inst, repoId)] });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Duplicates_are_rejected_case_insensitively()
    {
        var result = new CommunityInstallationOptionsValidator().Validate(null, new CommunityInstallationOptions { Entries = [E("a", "b"), E("A", "B")] });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Valid_entries_pass()
    {
        Assert.True(new CommunityInstallationOptionsValidator().Validate(null, new CommunityInstallationOptions { Entries = [E("a", "b"), E("a", "*")] }).Succeeded);
    }

    private static ServiceCollection BaseServices(Dictionary<string, string?>? config)
    {
        var services = new ServiceCollection();
        if (config is not null)
        {
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        }

        return services;
    }

    private static Dictionary<string, string?> Config(string installationId) => new()
    {
        ["Bazaar:Community:GitHub:Installations:0:Owner"] = "acme",
        ["Bazaar:Community:GitHub:Installations:0:Repo"] = "widgets",
        ["Bazaar:Community:GitHub:Installations:0:InstallationId"] = installationId,
        ["Bazaar:Community:GitHub:Installations:0:RepositoryId"] = "22",
    };

    [Fact]
    public async Task Registration_binds_configuration_and_defaults_to_configured_resolver()
    {
        var services = BaseServices(Config("11"));
        services.AddBazaarCommunityGitHubBridge();
        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<ICommunityInstallationResolver>();
        Assert.IsType<ConfiguredCommunityInstallationResolver>(resolver);
        Assert.Equal(new CommunityInstallation(11, 22), await resolver.ResolveAsync("Acme", "Widgets", CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("acme", "nope", CancellationToken.None));
    }

    [Fact]
    public void Registration_without_configuration_is_empty_allowlist()
    {
        var services = BaseServices(null);
        services.AddBazaarCommunityGitHubBridge();
        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetRequiredService<IOptions<CommunityInstallationOptions>>().Value.Entries);
    }

    [Fact]
    public void Invalid_configuration_fails_validation()
    {
        var services = BaseServices(Config("0"));
        services.AddBazaarCommunityGitHubBridge();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CommunityInstallationOptions>>().Value);
    }

    [Fact]
    public void Host_resolver_registered_before_or_after_wins()
    {
        var host = new HostResolver();
        var before = BaseServices(null);
        before.AddSingleton<ICommunityInstallationResolver>(host);
        before.AddBazaarCommunityGitHubBridge();
        using (var p = before.BuildServiceProvider())
        {
            Assert.Same(host, p.GetRequiredService<ICommunityInstallationResolver>());
        }

        var after = BaseServices(null);
        after.AddBazaarCommunityGitHubBridge();
        after.AddSingleton<ICommunityInstallationResolver>(host);
        using var p2 = after.BuildServiceProvider();
        Assert.Same(host, p2.GetRequiredService<ICommunityInstallationResolver>());
    }

    private sealed class HostResolver : ICommunityInstallationResolver
    {
        public Task<CommunityInstallation?> ResolveAsync(string owner, string repo, CancellationToken cancellationToken) => Task.FromResult<CommunityInstallation?>(null);
    }
}
