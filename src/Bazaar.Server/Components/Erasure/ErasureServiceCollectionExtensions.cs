using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Components.Erasure;

public static class ErasureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in key eraser (<see cref="PostgresPersonKeyEraser"/>) and <see cref="ErasureCoordinator"/>. The host
    /// must register an <c>NpgsqlDataSource</c> first. Register <see cref="IErasureParticipant"/> implementations before or after
    /// this call; the coordinator runs them in the order they were registered.
    /// </summary>
    public static IServiceCollection AddBazaarErasure(this IServiceCollection services)
    {
        services.AddSingleton<IPersonKeyEraser, PostgresPersonKeyEraser>();
        services.AddSingleton<ErasureCoordinator>();
        // TODO(component): register from host composition when the component model (PR #125) merges.
        return services;
    }
}
