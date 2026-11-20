using Bazaar.Core;
using Bazaar.Server.SealedGit;
using Bazaar.Server.SealedStorage;
using Bazaar.Server.Vault.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.SealedVault;

/// <summary>
/// The sealed Vault module (doc 61 "Vault adoption", PROPOSED). When <c>Bazaar:SealedVault:Enabled</c> is true it replaces the registered
/// <see cref="IGitProvider"/> with a <see cref="SealedGitProvider"/> that decorates it. Off by default until the plaintext import has been run.
/// </summary>
/// <remarks>
/// Configuration: <c>Bazaar:SealedVault:Enabled</c> (default false); <c>Bazaar:SealedVault:Name</c> the sealed repo name (scope
/// <c>vault:&lt;Name&gt;</c>); <c>Bazaar:SealedVault:MountRoot</c> the encrypted volume (doc 63) or tmpfs folder for the plaintext mount;
/// <c>Bazaar:SealedVault:ImportFrom</c> an optional plaintext checkout to import once (verify, then wipe). The repo and worktree paths are the
/// memory component's (<c>Memory:Vault:RepoPath</c>, <c>Memory:Vault:WorktreeRoot</c>) and must lie inside the mount root. Any missing or
/// unsafe value fails the start.
/// </remarks>
public sealed class SealedVaultComponent : IBazaarComponent
{
    public const string EnabledKey = "Bazaar:SealedVault:Enabled";
    public const string NameKey = "Bazaar:SealedVault:Name";
    public const string MountRootKey = "Bazaar:SealedVault:MountRoot";
    public const string ImportFromKey = "Bazaar:SealedVault:ImportFrom";

    // The memory component's keys (MemoryComponent.RepoPathKey and WorktreeRootKey); this module cannot reference it.
    private const string _repoPathKey = "Memory:Vault:RepoPath";
    private const string _worktreeRootKey = "Memory:Vault:WorktreeRoot";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "sealed-vault",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        // Hard edges set start order: the memory component registers the engine provider this one decorates.
        DependsOn: ["sealed-git", "memory"],
        Provides: ["security:sealed-vault"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!bool.TryParse(configuration[EnabledKey], out var enabled) || !enabled)
        {
            return;
        }

        var repoPath = Required(configuration, _repoPathKey);
        var worktreeRoot = configuration[_worktreeRootKey] is { Length: > 0 } configured
            ? configured
            : repoPath.TrimEnd('/', '\\') + ".memory-worktrees";
        var options = new SealedVaultOptions(
            Required(configuration, NameKey),
            Required(configuration, MountRootKey),
            repoPath,
            worktreeRoot,
            configuration[ImportFromKey] is { Length: > 0 } import ? import : null).Normalised();

        var engines = services.Where(d => d.ServiceType == typeof(IGitProvider) && !d.IsKeyedService).ToList();
        var engine = engines.LastOrDefault()
            ?? throw new InvalidOperationException($"{EnabledKey} is true but no IGitProvider is registered; the sealed Vault needs the memory Vault ({_repoPathKey}).");
        foreach (var descriptor in engines)
        {
            // Every unkeyed registration goes, so no caller can resolve the plaintext engine around the seal.
            services.Remove(descriptor);
        }

        services.AddSingleton(sp => new SealedGitProvider(
            CreateEngine(sp, engine),
            sp.GetRequiredService<ISealedGitRepoFactory>().Open(options.Name),
            options,
            sp.GetRequiredService<ISealAuditSink>()));
        services.AddSingleton<IGitProvider>(sp => sp.GetRequiredService<SealedGitProvider>());
        services.AddSingleton<ISealedVault>(sp => sp.GetRequiredService<SealedGitProvider>());
    }

    private static IGitProvider CreateEngine(IServiceProvider sp, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance as IGitProvider
        ?? (descriptor.ImplementationFactory is { } factory
            ? (IGitProvider)factory(sp)
            : (IGitProvider)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{key} is required when {EnabledKey} is true.");
}
