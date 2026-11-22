using Bazaar.Server.Vault.Data;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Index;
using Bazaar.Server.Vault.Links;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Setup.Constitution;

/// <summary>Registers the Constitution seed step and the Vault adapter behind it.</summary>
public static class ConstitutionServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="ConstitutionSeedStep"/> as an <see cref="ISetupStep"/>. Needs the shared <c>NpgsqlDataSource</c>. With
    /// <paramref name="options"/> null the step is still registered and fails with a message naming the missing settings.
    /// Services use TryAdd, so a host's own Vault registrations win. Nothing is resolved here.
    /// </summary>
    public static IServiceCollection AddConstitutionSeed(this IServiceCollection services, ConstitutionVaultOptions? options)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGitProvider, LibGit2GitProvider>();
        services.TryAddSingleton(sp => new VaultIndexer(
            sp.GetRequiredService<IGitProvider>(),
            new VaultIndexRepository(sp.GetRequiredService<NpgsqlDataSource>()),
            new VaultStateRepository(sp.GetRequiredService<NpgsqlDataSource>()),
            new VaultCommitRepository(sp.GetRequiredService<NpgsqlDataSource>()),
            VaultAmbiguousLinkPolicy.Warn));
        services.TryAddSingleton<IVaultCatchUp>(sp => new VaultIndexerCatchUp(sp.GetRequiredService<VaultIndexer>()));
        services.TryAddSingleton<IConstitutionVault>(sp => options is null
            ? new UnconfiguredConstitutionVault()
            : new GitConstitutionVault(options, sp.GetRequiredService<IGitProvider>(), sp.GetRequiredService<IVaultCatchUp>()));
        services.AddSingleton<ConstitutionSeedStep>();
        services.AddSingleton<ISetupStep>(sp => sp.GetRequiredService<ConstitutionSeedStep>());
        return services;
    }
}
