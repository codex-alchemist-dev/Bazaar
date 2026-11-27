using Bazaar.Approvals;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.GitHub.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.GitHub.Tests;

/// <summary>The GitHub module composes the adapter and the owner-signed merge gate (doc 21, Merge request path).</summary>
public sealed class GitHubRegistrationTests
{
    private sealed class NoKeys : IOwnerKeyRegistry
    {
        public Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult<OwnerDeviceKey?>(null);
    }

    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOwnerKeyRegistry, NoKeys>();
        services.AddSingleton<IOwnerNonceStore, InMemoryOwnerNonceStore>();
        services.AddSingleton<IOwnerSignatureVerifier, OwnerSignatureVerifier>();
        services.AddBazaarGitHub(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void The_merge_gate_and_the_adapter_resolve_from_the_module()
    {
        using var provider = Build();

        Assert.IsType<OwnerSignedMerge>(provider.GetRequiredService<IOwnerSignedMergeService>());
        Assert.Same(provider.GetRequiredService<IGitHubAdapter>(), provider.GetRequiredService<GitHubAdapter>());
    }

    [Fact]
    public void The_five_PR_wire_handlers_and_a_read_model_resolve_from_the_module()
    {
        using var provider = Build();

        var types = provider.GetServices<Bazaar.Server.Wire.Connection.IWireRequestHandler>().Select(h => h.Type).ToHashSet();

        Assert.Superset(
            new HashSet<string> { Bazaar.Contracts.Wire.WireTypes.PrList, Bazaar.Contracts.Wire.WireTypes.PrSummary, Bazaar.Contracts.Wire.WireTypes.PrHunks, Bazaar.Contracts.Wire.WireTypes.PrReview, Bazaar.Contracts.Wire.WireTypes.PrMerge },
            types);
        Assert.IsType<Bazaar.Server.GitHub.Wire.EmptyGitHubPrReadModel>(provider.GetRequiredService<Bazaar.Server.GitHub.Wire.IGitHubPrReadModel>());
    }

    [Fact]
    public async Task Without_an_identity_service_the_token_provider_fails_closed()
    {
        using var provider = Build();
        var tokens = provider.GetRequiredService<IGitHubTokenProvider>();

        Assert.IsType<UnconfiguredGitHubTokenProvider>(tokens);
        await Assert.ThrowsAsync<GitHubTokenException>(() =>
            tokens.GetTokenAsync(new GitHubTokenScope(1, [1], new Dictionary<string, string> { ["pull_requests"] = "write" }), CancellationToken.None));
    }

    [Fact]
    public void An_identity_service_address_selects_the_exchange_token_provider()
    {
        using var provider = Build(("Bazaar:GitHub:IdentityService:BaseAddress", "https://identity.example.test/"));

        Assert.IsType<IdentityServiceGitHubTokenProvider>(provider.GetRequiredService<IGitHubTokenProvider>());
    }

    [Fact]
    public void Adapter_options_are_read_from_configuration()
    {
        using var provider = Build(("Bazaar:GitHub:Adapter:MaxRetries", "5"));

        Assert.Equal(5, provider.GetRequiredService<GitHubAdapterOptions>().MaxRetries);
    }

    [Fact]
    public void The_webhook_processor_is_registered_and_stays_idle_without_a_real_sink()
    {
        using var provider = Build();

        var service = Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Bazaar.Server.GitHub.Processing.GitHubWebhookProcessingService>());
        Assert.NotNull(service.DisabledReason);
    }

    [Fact]
    public void The_webhook_processor_runs_with_a_real_sink_and_a_database()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOwnerKeyRegistry, NoKeys>();
        services.AddSingleton<IOwnerNonceStore, InMemoryOwnerNonceStore>();
        services.AddSingleton<IOwnerSignatureVerifier, OwnerSignatureVerifier>();
        services.AddSingleton<Bazaar.Server.GitHub.Ingress.IGitHubEventSink, NoopSink>();
        using var dataSource = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused");
        services.AddSingleton(dataSource);
        services.AddBazaarGitHub(configuration);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var service = Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Bazaar.Server.GitHub.Processing.GitHubWebhookProcessingService>());
        Assert.Null(service.DisabledReason);
    }

    private sealed class NoopSink : Bazaar.Server.GitHub.Ingress.IGitHubEventSink
    {
        public Task DeliverAsync(Bazaar.Server.GitHub.Ingress.GitHubEventDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void The_component_manifest_names_the_github_forge()
    {
        var manifest = new GitHubComponent().Manifest;

        Assert.Equal("github", manifest.Id);
        Assert.Contains("forge:github", manifest.Provides);
    }
}
