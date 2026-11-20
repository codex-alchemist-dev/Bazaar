using Bazaar.Server.CodingWorkspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Maps the coding workspace module's repository source onto the host's <see cref="ICodingRepoDirectory"/> (doc 17, "Workspace
/// sources"). A null source result stays null, which refuses the call.
/// </summary>
/// <param name="source">Where the repositories come from.</param>
public sealed class CodingRepoDirectoryAdapter(ICodingRepoSource source) : ICodingRepoDirectory
{
    private readonly ICodingRepoSource _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public async ValueTask<CodingRepoBinding?> FindAsync(string repoId, CancellationToken cancellationToken)
    {
        var record = await _source.FindAsync(repoId, cancellationToken).ConfigureAwait(false);
        return record is null
            ? null
            : new CodingRepoBinding(record.ClonePath, record.InstallationId, record.Owner, record.Name, record.RepositoryId);
    }
}

/// <summary>Registers the coding workspace sources for the harness (doc 17, "Workspace sources").</summary>
public static class CodingWorkspaceHostExtensions
{
    /// <summary>
    /// When <c>Coding:Harness:Enabled</c> is true, registers the work-share layout, the repository source, the
    /// <see cref="ICodingRepoDirectory"/> adapter and the subagent worktree allocator, all with TryAdd so a host registration made
    /// first wins. Settings come from <c>Coding:Workspace:Root</c> and <c>Coding:Workspace:Repos</c>. The host must still bind
    /// <see cref="ISubagentParentWorkSource"/>; until it does, the allocator fails when first resolved and no worktree is handed out.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration. Null means off.</param>
    public static IServiceCollection AddBazaarCodingWorkspaceSources(this IServiceCollection services, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!bool.TryParse(configuration?["Coding:Harness:Enabled"], out var enabled) || !enabled)
        {
            return services;
        }

        services.AddBazaarCodingWorkspace(options =>
        {
            options.Root = configuration!["Coding:Workspace:Root"];
            foreach (var section in configuration.GetSection("Coding:Workspace:Repos").GetChildren())
            {
                options.Repos.Add(new CodingRepoOptions
                {
                    Id = section["Id"],
                    DefaultBranch = section["DefaultBranch"] ?? "main",
                    ClonePath = section["ClonePath"],
                    InstallationId = long.TryParse(section["InstallationId"], out var installation) ? installation : 0,
                    Owner = section["Owner"],
                    Name = section["Name"],
                    RepositoryId = long.TryParse(section["RepositoryId"], out var repositoryId) ? repositoryId : 0,
                });
            }
        });
        services.TryAddSingleton<ICodingRepoDirectory>(sp => new CodingRepoDirectoryAdapter(sp.GetRequiredService<ICodingRepoSource>()));
        return services;
    }
}
