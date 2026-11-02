using Bazaar.Client.LocalInference.Onnx;
using Bazaar.Client.LocalInference.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Client.LocalInference.Tests.Onnx;

public class LocalInferenceServiceCollectionTests
{
    [Fact]
    public void AddBazaarLocalInference_resolves_the_store_cache_and_CPU_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-di-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection().AddBazaarLocalInference(root).BuildServiceProvider();

        Assert.Equal(Path.GetFullPath(root), services.GetRequiredService<LocalModelStore>().Root);
        Assert.IsType<InMemoryLocalResultCache>(services.GetRequiredService<ILocalResultCache>());
        Assert.IsType<OnnxCpuProvider>(services.GetRequiredService<IExecutionProviderSelector>());
    }

    [Fact]
    public void AddBazaarLocalInference_keeps_an_existing_registration()
    {
        var existing = new LocalModelStore(Path.Combine(Path.GetTempPath(), "bazaar-di-existing"));
        var services = new ServiceCollection();
        services.AddSingleton(existing);

        services.AddBazaarLocalInference(Path.Combine(Path.GetTempPath(), "bazaar-di-other"));

        Assert.Same(existing, services.BuildServiceProvider().GetRequiredService<LocalModelStore>());
    }

    [Fact]
    public void AddBazaarLocalInference_rejects_an_empty_root()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddBazaarLocalInference(" "));
    }
}
