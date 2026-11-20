using Bazaar.LoopGuard.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.LoopGuard;

/// <summary>Composition of the loop-guard trip handling module.</summary>
public static class LoopGuardRegistration
{
    /// <summary>
    /// Registers <see cref="LoopTripRecorder"/> as <see cref="ILoopTripHandler"/> and, unless the host already set one,
    /// <see cref="NoOpPsychWardGate"/> as <see cref="IPsychWardGate"/>. The host must register an <c>NpgsqlDataSource</c>, an
    /// <c>IGcLogWriter</c> and logging first.
    /// </summary>
    public static IServiceCollection AddBazaarLoopTripHandling(this IServiceCollection services)
    {
        services.TryAddSingleton<IPsychWardGate, NoOpPsychWardGate>();
        services.TryAddSingleton<ILoopTripHandler, LoopTripRecorder>();
        return services;
    }
}
