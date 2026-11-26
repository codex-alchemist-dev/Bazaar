using Bazaar.Server.DevSandbox.Wasm;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DevSandbox.Wasm.Wasmtime.Tests;

public sealed class WasmtimeSandboxModuleTests
{
    private sealed class Source : IWasmModuleSource
    {
        public ValueTask<ReadOnlyMemory<byte>?> GetModuleAsync(string moduleId, string moduleVersion, CancellationToken ct) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }

    [Fact]
    public void Without_a_module_source_nothing_is_registered()
    {
        var services = new ServiceCollection().AddBazaarDevSandboxWasmtime();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISandboxEngine));
    }

    [Fact]
    public void With_a_module_source_the_engine_resolves()
    {
        using var sp = new ServiceCollection().AddSingleton<IWasmModuleSource, Source>().AddBazaarDevSandboxWasmtime().BuildServiceProvider();

        var engine = Assert.Single(sp.GetServices<ISandboxEngine>());

        Assert.Equal(SandboxEngineKind.Wasm, engine.Kind);
    }
}
