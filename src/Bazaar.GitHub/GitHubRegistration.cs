using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.GitHub.Ingress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.GitHub;

public static class GitHubRegistration
{
    /// <summary>Configuration section for the adapter options and the Identity Service token source.</summary>
    public const string ConfigurationSection = "Bazaar:GitHub";

    /// <summary>
    /// Registers the GitHub adapter and the owner-signed merge gate (doc 21). The gate needs an <see cref="Bazaar.Approvals.IOwnerSignatureVerifier"/>,
    /// which <c>AddBazaarApprovals</c> supplies. Tokens come from the Identity Service when
    /// <c>Bazaar:GitHub:IdentityService:BaseAddress</c> is set; otherwise the token provider fails closed, so nothing is sent.
    /// Every registration is TryAdd, so a host or a test can substitute a part first.
    /// </summary>
    public static IServiceCollection AddBazaarGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        services.TryAddSingleton(section.GetSection("Adapter").Get<GitHubAdapterOptions>() ?? new GitHubAdapterOptions());
        services.TryAddSingleton(section.GetSection("Merge").Get<GitHubMergeOptions>() ?? new GitHubMergeOptions());
        services.TryAddSingleton<IGitHubRateState>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgGitHubRateState(dataSource) : new InMemoryGitHubRateState());

        // Events poller (doc 21, Ingress). Off until Ingress:InstallationId, Ingress:Repositories and a real sink exist.
        services.TryAddSingleton(section.GetSection("Ingress").Get<GitHubIngressOptions>() ?? new GitHubIngressOptions());
        services.TryAddSingleton<IGitHubGcDirectory>(sp =>
            new PgGitHubGcDirectory(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<GitHubIngressOptions>()));
        services.TryAddSingleton<IGitHubInboxWriter>(sp =>
            new InboxRepositoryGitHubInboxWriter(new Bazaar.Server.Agents.Data.InboxRepository(sp.GetRequiredService<NpgsqlDataSource>())));
        // The real sink needs the GC writer, and a database (the bridge principal is seeded, see GitHubBridgePrincipal); otherwise the poller stays off (Unwired).
        services.TryAddSingleton<IGitHubEventSink>(sp =>
            sp.GetService<Bazaar.Server.Gc.Write.IGcLogWriter>() is { } writer
            && sp.GetService<NpgsqlDataSource>() is not null
                ? new GitHubGcEventSink(writer, sp.GetRequiredService<IGitHubInboxWriter>(), sp.GetRequiredService<IGitHubGcDirectory>(),
                    sp.GetRequiredService<GitHubIngressOptions>(), sp.GetRequiredService<ILogger<GitHubGcEventSink>>())
                : new UnwiredGitHubEventSink());
        services.TryAddSingleton<IGitHubDeliveryStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgGitHubDeliveryStore(dataSource) : new InMemoryGitHubDeliveryStore());
        services.TryAddSingleton(sp => new GitHubEventsPoller(
            sp.GetRequiredService<IGitHubAdapter>(),
            sp.GetRequiredService<IGitHubEventSink>(),
            sp.GetRequiredService<IGitHubDeliveryStore>(),
            sp.GetRequiredService<GitHubIngressOptions>(),
            sp.GetRequiredService<ILogger<GitHubEventsPoller>>(),
            sp.GetService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GitHubEventsPollingService>(sp => new GitHubEventsPollingService(
            sp.GetRequiredService<GitHubEventsPoller>(),
            sp.GetRequiredService<GitHubIngressOptions>(),
            sp.GetRequiredService<IGitHubEventSink>(),
            sp.GetRequiredService<ILogger<GitHubEventsPollingService>>(),
            sp.GetService<TimeProvider>())));
        services.TryAddSingleton(section.GetSection("Webhook").Get<Bazaar.GitHub.Webhooks.GitHubWebhookOptions>() ?? new Bazaar.GitHub.Webhooks.GitHubWebhookOptions());

        var identityBase = section["IdentityService:BaseAddress"];
        if (string.IsNullOrWhiteSpace(identityBase))
        {
            services.TryAddSingleton<IGitHubTokenProvider, UnconfiguredGitHubTokenProvider>();
        }
        else
        {
            var identityOptions = section.GetSection("IdentityService").Get<IdentityServiceGitHubTokenOptions>() ?? new IdentityServiceGitHubTokenOptions();
            var identityUri = new Uri(identityBase, UriKind.Absolute);
            services.TryAddSingleton<IIdentityServiceTokenClient>(_ =>
                new HttpIdentityServiceTokenClient(new HttpClient { BaseAddress = identityUri }, identityOptions));
            services.TryAddSingleton<IGitHubTokenProvider>(sp =>
                new IdentityServiceGitHubTokenProvider(sp.GetRequiredService<IIdentityServiceTokenClient>(), sp.GetService<TimeProvider>()));
        }

        // One adapter serves the public IGitHubAdapter and the internal merge executor.
        services.TryAddSingleton(sp => new GitHubAdapter(
            new HttpClient(),
            sp.GetRequiredService<IGitHubTokenProvider>(),
            sp.GetRequiredService<IGitHubRateState>(),
            sp.GetRequiredService<GitHubAdapterOptions>(),
            sp.GetRequiredService<ILogger<GitHubAdapter>>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IGitHubAdapter>(sp => sp.GetRequiredService<GitHubAdapter>());
        services.TryAddSingleton<IOwnerSignedMergeService>(sp => new OwnerSignedMerge(
            sp.GetRequiredService<IGitHubAdapter>(),
            sp.GetRequiredService<GitHubAdapter>(),
            sp.GetRequiredService<Bazaar.Approvals.IOwnerSignatureVerifier>(),
            sp.GetService<TimeProvider>()));
        return services;
    }
}
