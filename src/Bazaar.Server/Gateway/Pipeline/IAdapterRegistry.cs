using System.Diagnostics.CodeAnalysis;
using Bazaar.Server.Gateway.Adapters;

namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>Finds the adapter for a provider row's <c>adapter</c> name (<c>openai_chat</c>, <c>anthropic_messages</c>, <c>gemini_generate_content</c>).</summary>
public interface IAdapterRegistry
{
    /// <summary>The adapter registered under the name, or null when the name is unknown.</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "Get is the agreed seam name.")]
    IModelAdapter? Get(string adapterName);
}

/// <summary>A dictionary-backed <see cref="IAdapterRegistry"/>. Names are matched exactly.</summary>
public sealed class DictionaryAdapterRegistry(IReadOnlyDictionary<string, IModelAdapter> adapters) : IAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IModelAdapter> _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));

    public IModelAdapter? Get(string adapterName) => _adapters.GetValueOrDefault(adapterName);
}
