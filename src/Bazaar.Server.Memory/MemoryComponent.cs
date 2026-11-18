using Bazaar.Core;
using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Search;
using Bazaar.Server.Vault.Writes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Memory.GitVault;

/// <summary>
/// Agent memory on the Vault as a Bazaar Component (docs/design/08-memory-system.md, section 11). It registers the Vault-backed
/// <see cref="IMemoryVault"/> and the memory <see cref="IMemoryBatchSink"/>, which is the same vault wrapped by the Constellation
/// graph sink when the graph store is registered. It does nothing until <c>Memory:Vault:RepoPath</c> is set, and it fails fast at
/// startup when the path is set but the rest of the configuration is incomplete.
/// </summary>
/// <remarks>
/// Configuration (install level, paths and identities are host facts, not registry settings):
/// <c>Memory:Vault:RepoPath</c> the Vault's main checkout;
/// <c>Memory:Vault:WorktreeRoot</c> where batch worktrees are made (default: a sibling folder <c>&lt;RepoPath&gt;.memory-worktrees</c>);
/// <c>Memory:Vault:WriterName</c> and <c>Memory:Vault:WriterEmail</c> the committer of merges (the writer);
/// <c>Memory:Vault:AuthorEmailFormat</c> the author email of a batch, with <c>{agent}</c> replaced by the agent id (doc 18, commit attribution);
/// <c>Memory:Vault:VaultId</c> the Vault id for lexical search (without it, search returns no candidates).
/// There is no default identity or domain.
/// </remarks>
public sealed class MemoryComponent : IBazaarComponent
{
    public const string RepoPathKey = "Memory:Vault:RepoPath";
    public const string WorktreeRootKey = "Memory:Vault:WorktreeRoot";
    public const string WriterNameKey = "Memory:Vault:WriterName";
    public const string WriterEmailKey = "Memory:Vault:WriterEmail";
    public const string AuthorEmailFormatKey = "Memory:Vault:AuthorEmailFormat";
    public const string VaultIdKey = "Memory:Vault:VaultId";

    private const string _agentToken = "{agent}";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "memory",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:memory"],
        // The search index and the graph store are optional: without them search is empty and memory writes are not projected.
        SoftDependsOn: ["vault", "constellation"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var repoPath = configuration[RepoPathKey];
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return;
        }

        var writerName = Required(configuration, WriterNameKey);
        var writerEmail = Required(configuration, WriterEmailKey);
        var authorFormat = Required(configuration, AuthorEmailFormatKey);
        if (!authorFormat.Contains(_agentToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{AuthorEmailFormatKey} must contain {_agentToken}.");
        }

        var worktreeRoot = configuration[WorktreeRootKey] is { Length: > 0 } configured
            ? configured
            : repoPath.TrimEnd('/', '\\') + ".memory-worktrees";
        var options = new GitMemoryVaultOptions(
            repoPath,
            worktreeRoot,
            agent => new GitIdentity(agent, authorFormat.Replace(_agentToken, agent, StringComparison.Ordinal)));
        Guid? vaultId = Guid.TryParse(configuration[VaultIdKey], out var parsed) ? parsed : null;

        services.TryAddSingleton<IGitProvider, LibGit2GitProvider>();
        services.TryAddSingleton(sp => new VaultWriter(
            sp.GetRequiredService<IGitProvider>(),
            repoPath,
            new GitIdentity(writerName, writerEmail),
            sp.GetService<IVaultCommitObserver>()));
        services.TryAddSingleton<IVaultWriter>(sp => sp.GetRequiredService<VaultWriter>());

        services.TryAddSingleton<IMemoryLexicalIndex>(sp => vaultId is { } id && sp.GetService<VaultSearchService>() is { } search
            ? new VaultSearchLexicalIndex(search, id)
            : new NullMemoryLexicalIndex());
        services.TryAddSingleton<GitMemoryVault>(sp => new GitMemoryVault(
            sp.GetRequiredService<IGitProvider>(),
            sp.GetRequiredService<IVaultWriter>(),
            sp.GetRequiredService<IMemoryLexicalIndex>(),
            options));
        services.TryAddSingleton<IMemoryVault>(sp => sp.GetRequiredService<GitMemoryVault>());

        // Writes go to the Vault first; the graph sink then projects them when a graph store exists.
        services.TryAddSingleton<IMemoryBatchSink>(sp => sp.GetService<IMemoryGraphStore>() is { } graph
            ? MemoryGraphSink.Wrap(sp.GetRequiredService<IMemoryVault>(), graph, sp.GetRequiredService<ILogger<MemoryGraphSink>>())
            : sp.GetRequiredService<IMemoryVault>());
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{key} is required when {RepoPathKey} is set.");
}
