using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.GitHub.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.GitHub;

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
        services.TryAddSingleton<IGitHubRateState, InMemoryGitHubRateState>();

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
