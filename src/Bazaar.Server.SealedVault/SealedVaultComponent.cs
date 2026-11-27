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
/// unsafe value fails the start. <c>Bazaar:SealedVault:MountRootAttested</c> (default false) is the operator's attestation that the mount root
/// is the encrypted volume; without it only a detected tmpfs is accepted. <c>Bazaar:SealedVault:AdoptStaleMount</c> (default false) reseals a
/// crashed mount whose refs are not sealed instead of refusing it. Disabled, the start is refused while a mount marker sits next to the Vault
/// path. Only unkeyed <see cref="IGitProvider"/> registrations made before this component runs are decorated; a keyed one, or one added later,
/// is not, and must not point at the Vault path.
/// </remarks>
public sealed class SealedVaultComponent : IBazaarComponent
{
    public const string EnabledKey = "Bazaar:SealedVault:Enabled";
    public const string NameKey = "Bazaar:SealedVault:Name";
    public const string MountRootKey = "Bazaar:SealedVault:MountRoot";
    public const string ImportFromKey = "Bazaar:SealedVault:ImportFrom";
    public const string MountRootAttestedKey = "Bazaar:SealedVault:MountRootAttested";
    public const string AdoptStaleMountKey = "Bazaar:SealedVault:AdoptStaleMount";

    // SealedGitRepo.MountMarkerSuffix: the marker next to a folder a sealed mount owns.
    private const string _markerSuffix = ".bazaar-sealed-mount";

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
            RefuseDisabledStartOverAMount(configuration);
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
            configuration[ImportFromKey] is { Length: > 0 } import ? import : null,
            Flag(configuration, AdoptStaleMountKey)).Normalised();
        var attested = Flag(configuration, MountRootAttestedKey);

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
            sp.GetRequiredService<ISealAuditSink>(),
            sp.GetService<IMountRootVerifier>() ?? new ProcMountsRootVerifier(attested)));
        services.AddSingleton<IGitProvider>(sp => sp.GetRequiredService<SealedGitProvider>());
        services.AddSingleton<ISealedVault>(sp => sp.GetRequiredService<SealedGitProvider>());
    }

    /// <summary>
    /// A sealed mount left at the Vault path (its marker exists) would be used as a plain repo by the unsealed engine, so its commits would never be
    /// sealed and its plaintext would stay. Disabling the sealed Vault while a mount (or a crashed one) is there fails the start.
    /// </summary>
    private static void RefuseDisabledStartOverAMount(IConfiguration configuration)
    {
        if (configuration[_repoPathKey] is not { Length: > 0 } repoPath)
        {
            return;
        }

        var worktreeRoot = configuration[_worktreeRootKey] is { Length: > 0 } configured ? configured : repoPath.TrimEnd('/', '\\') + ".memory-worktrees";
        foreach (var folder in new[] { repoPath, worktreeRoot })
        {
            var marker = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + _markerSuffix;
            if (File.Exists(marker))
            {
                throw new InvalidOperationException(
                    $"{EnabledKey} is false but {folder} is a sealed Vault mount (marker {marker}). Enable the sealed Vault so it is checked and wiped, "
                    + "or export the sealed repo deliberately; the start is refused so the plaintext is not used unsealed.");
            }
        }
    }

    private static bool Flag(IConfiguration configuration, string key) => bool.TryParse(configuration[key], out var value) && value;

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
