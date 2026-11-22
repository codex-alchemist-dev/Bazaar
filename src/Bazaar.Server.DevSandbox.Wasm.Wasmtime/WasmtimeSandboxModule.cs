using Bazaar.Server.DevSandbox.Wasm;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DevSandbox.Wasm.Wasmtime;

/// <summary>Registers the WebAssembly engine over Wasmtime (doc 48 section 2.1, WebAssembly engine).</summary>
public static class WasmtimeSandboxModule
{
    /// <summary>
    /// Adds the Wasmtime runtime and the engine. The module source (where a module's bytes come from) must already be registered as an
    /// <see cref="IWasmModuleSource"/>; without one nothing is added and Wasm runs are refused.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Fuel, memory and import limits; null uses the defaults.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBazaarDevSandboxWasmtime(this IServiceCollection services, WasmEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(d => d.ServiceType == typeof(IWasmModuleSource)))
        {
            return services;
        }

        services.AddSingleton<IWasmRuntime, WasmtimeRuntime>();
        services.AddSingleton<ISandboxEngine>(sp =>
            new WasmSandboxEngine(sp.GetRequiredService<IWasmRuntime>(), sp.GetRequiredService<IWasmModuleSource>(), options));
        return services;
    }
}
