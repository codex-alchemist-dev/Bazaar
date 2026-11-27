using Cdax.Core;
using Bazaar.Server.SealedStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.SealedGit;

/// <summary>The sealed git module (doc 61 Q1): a bare git repo whose files live in a sealed scope. Temp root from <c>Bazaar:SealedGit:TempRoot</c>.</summary>
public sealed class SealedGitComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sealed-git",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["sealed-storage"],
        Provides: ["security:sealed-git"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var options = new SealedGitOptions { TempRoot = configuration["Bazaar:SealedGit:TempRoot"] };
        services.TryAddSingleton<ISealedGitRepoFactory>(sp => new SealedGitRepoFactory(
            sp.GetRequiredService<ISealedStore>(),
            sp.GetRequiredService<ISealAuditSink>(),
            options));
    }
}
