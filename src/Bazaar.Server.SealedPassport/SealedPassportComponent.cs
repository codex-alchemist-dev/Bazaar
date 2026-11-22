using Bazaar.Core;
using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.SealedGit;
using Bazaar.Server.SealedStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.SealedPassport;

/// <summary>
/// The sealed Passport module (doc 61 work package 4). It registers the sealed profile store as the Browser module's profile directory and
/// cookie source, the plaintext profile folders and the import. Sealing is a floor, not a setting, so there is no switch to turn it off.
/// </summary>
/// <remarks>
/// Configuration (<c>Bazaar:SealedPassport</c>): <c>AllowPlaintextProfileFolders</c> (default false; without it no plaintext profile folder can be opened), <c>MountRoot</c> (optional; also needed),
/// <c>LeaseTimeoutSeconds</c> (default 30), <c>ExcludedFolders</c> (Chromium cache folder names never sealed; default list in
/// <see cref="SealedPassportOptions"/>). The mount root is confirmed by the registered <see cref="IMountRootVerifier"/>; none registered means
/// every open is refused.
/// </remarks>
public sealed class SealedPassportComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sealed-passport",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["sealed-storage"],
        Provides: ["security:sealed-passport"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = ReadOptions(configuration.GetSection(SealedPassportOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(sp => new SealedPassportStore(sp.GetRequiredService<ISealedStore>(), sp.GetRequiredService<ISealAuditSink>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISealedPassportStore>(sp => sp.GetRequiredService<SealedPassportStore>());
        services.AddSingleton<IBrowserProfileDirectory>(sp => sp.GetRequiredService<SealedPassportStore>());
        services.AddSingleton<IBrowserCookieSource>(sp => new SealedBrowserCookieSource(sp.GetRequiredService<SealedPassportStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISealedProfileDirectories>(sp => new SealedProfileDirectories(
            sp.GetRequiredService<ISealedStore>(), sp.GetRequiredService<SealedPassportStore>(), options, sp.GetService<IMountRootVerifier>()));
        services.AddSingleton<ISealedPassportImporter>(sp => new SealedPassportImporter(sp.GetRequiredService<ISealedStore>(), sp.GetRequiredService<SealedPassportStore>(), options));
        services.AddSingleton<IHostedService>(sp => new ProfileFolderSweeper(sp.GetRequiredService<ISealedProfileDirectories>()));
    }

    internal static SealedPassportOptions ReadOptions(IConfigurationSection section)
    {
        var defaults = new SealedPassportOptions();
        var timeout = int.TryParse(section["LeaseTimeoutSeconds"], out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : defaults.LeaseTimeout;
        var excluded = section.GetSection("ExcludedFolders").GetChildren().Select(c => c.Value).OfType<string>().Where(v => v.Length > 0).ToArray();
        return new SealedPassportOptions
        {
            MountRoot = section["MountRoot"] is { Length: > 0 } root ? root : null,
            AllowPlaintextProfileFolders = bool.TryParse(section["AllowPlaintextProfileFolders"], out var allow) && allow,
            LeaseTimeout = timeout,
            ExcludedFolders = section.GetSection("ExcludedFolders").Exists() ? excluded : defaults.ExcludedFolders,
        };
    }
}
