using Bazaar.Server.Modules.Health;
using Bazaar.Souls.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Souls;

/// <summary>Composition of the Souls binding module.</summary>
public static class SoulBindingRegistration
{
    /// <summary>
    /// Registers <see cref="PostgresSoulBindings"/> as <see cref="ISoulBindings"/> and <see cref="ISoulBindingAdmin"/>, and the
    /// stamp reconciliation (<see cref="SoulStampReconciler"/>, <see cref="SoulBindingsHealth"/>). The host composes this; GC and
    /// the agent runtime depend only on the abstractions. The host must register an <c>NpgsqlDataSource</c> first.
    /// </summary>
    public static IServiceCollection AddBazaarSoulBindings(this IServiceCollection services)
    {
        services.AddSingleton<PostgresSoulBindings>();
        services.AddSingleton<ISoulBindings>(sp => sp.GetRequiredService<PostgresSoulBindings>());
        services.AddSingleton<ISoulBindingAdmin>(sp => sp.GetRequiredService<PostgresSoulBindings>());
        services.AddSingleton<SoulStampReconciler>();
        services.AddSingleton<SoulBindingsHealth>();
        services.AddSingleton<IModuleHealthContract>(sp => sp.GetRequiredService<SoulBindingsHealth>());
        return services;
    }
}
