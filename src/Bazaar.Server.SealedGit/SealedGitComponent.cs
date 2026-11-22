using Bazaar.Core;
using Bazaar.Server.SealedStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.SealedGit;

/// <summary>The sealed git module (doc 61 Q1): a bare git repo whose files live in a sealed scope. Settings: <c>Bazaar:SealedGit:TempRoot</c>, <c>GitPath</c> and <c>LeaseTimeout</c> (a time span, default 00:02:00).</summary>
public sealed class SealedGitComponent : IBazaarComponent
{
    public const string TempRootKey = "Bazaar:SealedGit:TempRoot";
    public const string GitPathKey = "Bazaar:SealedGit:GitPath";
    public const string LeaseTimeoutKey = "Bazaar:SealedGit:LeaseTimeout";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sealed-git",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["sealed-storage"],
        Provides: ["security:sealed-git"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var leaseTimeout = configuration[LeaseTimeoutKey] is { Length: > 0 } text
            ? TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero
                ? parsed
                : throw new InvalidOperationException($"{LeaseTimeoutKey} must be a positive time span such as 00:02:00.")
            : new SealedGitOptions().LeaseTimeout;
        var options = new SealedGitOptions
        {
            TempRoot = configuration[TempRootKey] is { Length: > 0 } tempRoot ? tempRoot : null,
            GitPath = configuration[GitPathKey] is { Length: > 0 } gitPath ? gitPath : null,
            LeaseTimeout = leaseTimeout,
        };
        services.TryAddSingleton<ISealedGitRepoFactory>(sp => new SealedGitRepoFactory(
            sp.GetRequiredService<ISealedStore>(),
            sp.GetRequiredService<ISealAuditSink>(),
            options));
    }
}
