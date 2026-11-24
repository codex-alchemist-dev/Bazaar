using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class CodingHarnessHostExtensionsTests
{
    private static ServiceCollection WithPolicy()
    {
        var registry = CapabilityRegistry.Create([]);
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton<IToolPolicyEngine>(new ToolPolicyEngine(registry, new InMemoryRateBucketStore(), PolicyOptions.Default));
        services.AddSingleton<IToolCallTrace, InMemoryToolCallTrace>();
        services.AddSingleton<IAgentFreeze, InMemoryAgentFreeze>();
        return services;
    }

    [Fact]
    public void The_permission_gate_resolves_over_the_policy_engine_adapter()
    {
        var services = WithPolicy();
        services.AddBazaarCodingHarness(CodingHarnessTestFakes.Config(true));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ToolPolicyEngineAdapter>(provider.GetRequiredService<IGenericToolPolicy>());
        Assert.IsType<ToolPermissionGate>(provider.GetRequiredService<IToolPermissionGate>());
    }

    [Fact]
    public void Ports_the_host_must_supply_fail_resolution_instead_of_defaulting_open()
    {
        var services = WithPolicy();
        services.AddBazaarCodingHarness(CodingHarnessTestFakes.Config(true));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IRuntimeSubagentPort>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<ICodingGitEngine>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IHostPushPort>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<ISubagentSpawner>);
    }

    [Fact]
    public void The_edit_module_registers_without_a_guest_channel_because_that_is_per_session()
    {
        var services = WithPolicy();
        services.AddBazaarCodingHarness(CodingHarnessTestFakes.Config(true));

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGuestFileChannel));
    }

    [Fact]
    public void The_github_adapter_defaults_to_api_github_com()
    {
        var services = WithPolicy();
        services.AddBazaarCodingHarness(CodingHarnessTestFakes.Config(true));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(new Uri("https://api.github.com/"), provider.GetRequiredService<GitHubAdapterOptions>().BaseAddress);
    }

    [Fact]
    public void The_github_api_base_address_is_read_from_the_module_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(CodingHarnessTestFakes.Config(true))
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Bazaar:GitHub:Adapter:BaseAddress", "https://ghe.example.test/api/v3/")])
            .Build();
        var services = WithPolicy();
        services.AddBazaarCodingHarness(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(new Uri("https://ghe.example.test/api/v3/"), provider.GetRequiredService<GitHubAdapterOptions>().BaseAddress);
    }

    [Fact]
    public void An_http_github_api_base_address_is_refused_by_the_adapter()
    {
        var options = new GitHubAdapterOptions { BaseAddress = new Uri("http://ghe.example.test/api/v3/") };

        Assert.Throws<ArgumentException>(() => new GitHubAdapter(
            new HttpClient(), new FakeTokens(), new InMemoryGitHubRateState(), options, NullLogger<GitHubAdapter>.Instance));
    }

    private sealed class FakeTokens : IGitHubTokenProvider
    {
        public Task<GitHubToken> GetTokenAsync(GitHubTokenScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubToken("t", DateTimeOffset.UtcNow.AddMinutes(5)));
    }
}
