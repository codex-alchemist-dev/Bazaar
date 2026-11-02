using Bazaar.Server.Components;
using Bazaar.Server.Kanban.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Kanban;

/// <summary>
/// Kanban as a Bazaar Component (docs/design/12-kanban-and-dependencies.md, docs/design/BAZAAR-COMPONENTS.md). Registers the task
/// repositories and <see cref="TaskService"/>. It needs the shared <c>NpgsqlDataSource</c> from the kernel and depends on no other component.
/// </summary>
public sealed class KanbanComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "kanban",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:kanban"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<TaskRepository>();
        services.TryAddSingleton<TaskEdgeRepository>();
        services.TryAddSingleton<TaskEventRepository>();
        services.TryAddSingleton<TaskLinkRepository>();
        services.TryAddSingleton<TaskService>();
    }
}
