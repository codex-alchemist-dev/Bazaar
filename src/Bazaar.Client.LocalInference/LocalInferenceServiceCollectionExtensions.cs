using Bazaar.Client.LocalInference.Onnx;
using Bazaar.Client.LocalInference.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Client.LocalInference;

/// <summary>
/// Registration for the Local Co-Worker inference core (doc 31). It registers the stable pieces: the model store, the
/// result cache and the CPU execution provider. Backends are built per model at run time (see <see cref="IExecutionProviderSelector"/>),
/// so the scheduler is created by the caller once a backend is selected.
/// </summary>
public static class LocalInferenceServiceCollectionExtensions
{
    /// <summary>Adds the local inference core, with model files under <paramref name="modelRoot"/>. Existing registrations are kept.</summary>
    public static IServiceCollection AddBazaarLocalInference(this IServiceCollection services, string modelRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRoot);

        services.TryAddSingleton(new LocalModelStore(modelRoot));
        services.TryAddSingleton<ILocalResultCache, InMemoryLocalResultCache>();
        services.TryAddSingleton<IExecutionProviderSelector>(sp => new OnnxCpuProvider(sp.GetRequiredService<LocalModelStore>()));

        return services;
    }
}
