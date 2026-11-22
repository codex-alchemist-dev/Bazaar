using System.Text.RegularExpressions;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingWorkspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Coding;

/// <summary>Binds the workspace module's <see cref="ICodingWorktreeRegistrar"/> port to the host's <see cref="CodingWorktreeRegistry"/>.</summary>
/// <param name="registry">The registry the tool host context reads.</param>
public sealed class CodingWorktreeRegistryRegistrar(CodingWorktreeRegistry registry) : ICodingWorktreeRegistrar
{
    private readonly CodingWorktreeRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public IDisposable Register(string worktreeKey, CodingWorktree worktree) => _registry.Register(worktreeKey, worktree);
}

/// <summary>Registers the per-task worktree flow and reads the default repo (doc 17, "Default repo and per-task worktree flow").</summary>
public static partial class CodingTaskWorkspaceHostExtensions
{
    /// <summary>The operator setting that names the repo the harness works on when a task names none.</summary>
    public const string RepoKey = "Coding:Harness:Repo";

    /// <summary>
    /// Reads and checks <see cref="RepoKey"/>: required, and one repo id of <c>[A-Za-z0-9_][A-Za-z0-9._-]{0,99}</c> not ending in a dot.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is missing or malformed. The message names the key, not the value.</exception>
    public static string ReadDefaultRepo(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[RepoKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{RepoKey} is required when Coding:Harness:Enabled is true: set the id of the repo the harness works on by default.");
        }

        if (!IdPattern().IsMatch(value) || value.EndsWith('.'))
        {
            throw new InvalidOperationException($"{RepoKey} is not a valid repo id: use 1 to 100 characters of [A-Za-z0-9._-], starting with a letter, digit or underscore.");
        }

        return value;
    }

    /// <summary>
    /// When <c>Coding:Harness:Enabled</c> is true, checks <see cref="RepoKey"/> (throws at registration, naming the key) and registers
    /// <see cref="ICodingTaskWorkspace"/>, the shared <see cref="CodingWorktreeRegistry"/> and its registrar, all with TryAdd. Does
    /// nothing when the harness is off.
    /// </summary>
    public static IServiceCollection AddBazaarCodingTaskWorkspace(this IServiceCollection services, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!bool.TryParse(configuration?["Coding:Harness:Enabled"], out var enabled) || !enabled)
        {
            return services;
        }

        var repo = ReadDefaultRepo(configuration!);
        services.TryAddSingleton<CodingWorktreeRegistry>();
        services.TryAddSingleton<ICodingWorktreeDirectory>(sp => sp.GetRequiredService<CodingWorktreeRegistry>());
        services.TryAddSingleton<ICodingWorktreeRegistrar>(sp => new CodingWorktreeRegistryRegistrar(sp.GetRequiredService<CodingWorktreeRegistry>()));
        services.TryAddSingleton<ICodingTaskWorkspace>(sp => new CodingTaskWorkspace(
            sp.GetRequiredService<CodingGitService>(),
            sp.GetRequiredService<ICodingRepoSource>(),
            sp.GetRequiredService<IWorkShareLayout>(),
            sp.GetRequiredService<ICodingWorktreeRegistrar>(),
            repo));
        return services;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
