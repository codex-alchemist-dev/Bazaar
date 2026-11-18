using Bazaar.Core;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Paths;
using Bazaar.Server.VmMounts.Profiles;
using Bazaar.Server.VmMounts.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmMounts;

/// <summary>
/// Scoped VM mounts (docs/design/16-agent-vms-and-environments.md WP8) as a Bazaar Component. It owns no tables: <c>scoped_mount</c>
/// belongs to the <c>vms</c> component, so the manifest depends on it and adds no migration.
/// </summary>
/// <remarks>
/// The host must also register: <c>NpgsqlDataSource</c>, <c>IAuditLog</c>, <see cref="IMountProvider"/> (the OS provider),
/// <see cref="IMountGuestChannel"/> (an adapter over the transport module) and <see cref="ICanarySink"/> (agent safety). Without
/// them the manager cannot be resolved, which is the safe failure. <see cref="IMountContentPolicy"/> defaults to
/// <see cref="DefaultMountContentPolicy"/> until the Identity mount allow-list is adapted to it.
/// </remarks>
public sealed class VmMountsComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-mounts",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["vms", "audit"],
        Provides: ["feature:vm-mounts"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddBazaarVmMounts(configuration);
}

/// <summary>Registration helper for the scoped-mount module.</summary>
public static class VmMountsRegistration
{
    /// <summary>
    /// Registers the manager and its default parts. Settings come from the <c>VmMounts</c> section: <c>AgentsRoot</c> (required),
    /// <c>DenyPaths</c> (list) and <c>CaseInsensitive</c>. Everything else keeps the shipped defaults.
    /// </summary>
    public static IServiceCollection AddBazaarVmMounts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("VmMounts");
        var root = section["AgentsRoot"];
        var deny = section.GetSection("DenyPaths").GetChildren().Select(c => c.Value).OfType<string>().ToArray();
        var caseInsensitive = bool.TryParse(section["CaseInsensitive"], out var ci) ? ci : OperatingSystem.IsWindows();
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("VmMounts:AgentsRoot is not configured; scoped mounts cannot start without it.");

        services.TryAddSingleton(new VmMountOptions { AgentsRoot = root, DenyPaths = deny, CaseInsensitive = caseInsensitive });
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMountContentPolicy, DefaultMountContentPolicy>();
        services.TryAddSingleton<IScopedMountStore, PostgresScopedMountStore>();
        services.TryAddSingleton<IProfileTransfer, GuestProfileTransfer>();
        services.TryAddSingleton<IMountManager, MountManager>();
        return services;
    }
}
