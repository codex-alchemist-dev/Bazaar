using Bazaar.Server.Secrets;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Embeddings;

/// <summary>Builds the configured providers once and serves them by id. A bad entry fails at construction, so startup shows it.</summary>
public sealed class EmbeddingProviderRegistry : IEmbeddingProviderRegistry, IDisposable
{
    private readonly Dictionary<string, IEmbeddingProvider> _providers = new(StringComparer.Ordinal);
    private readonly List<HttpClient> _clients = [];

    public EmbeddingProviderRegistry(
        IOptions<EmbeddingOptions> options,
        IGatewayEmbeddingClient? gateway = null,
        ISecretResolver? secrets = null,
        Func<EmbeddingProviderOptions, HttpClient>? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var entry in options.Value.Providers)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                throw new InvalidOperationException("An embedding provider needs an Id.");
            }

            if (_providers.ContainsKey(entry.Id))
            {
                throw new InvalidOperationException($"Embedding provider id '{entry.Id}' is configured twice.");
            }

            _providers[entry.Id] = entry.Kind switch
            {
                EmbeddingProviderKind.Fake => new FakeEmbeddingProvider(entry.Dimensions, string.IsNullOrWhiteSpace(entry.ModelId) ? FakeEmbeddingProvider.DefaultModelId : entry.ModelId),
                EmbeddingProviderKind.OpenAiCompatible => new OpenAiCompatibleEmbeddingProvider(CreateClient(entry, httpClientFactory), entry, secrets),
                EmbeddingProviderKind.Gateway => new GatewayEmbeddingProvider(
                    gateway ?? throw new InvalidOperationException(
                        $"Embedding provider '{entry.Id}' uses the gateway, which has no embeddings call yet (blocked on the Gateway work)."),
                    entry),
                _ => throw new InvalidOperationException($"Embedding provider '{entry.Id}' has an unknown kind."),
            };
        }
    }

    public IReadOnlyCollection<string> ProviderIds => _providers.Keys;

    public bool TryGet(string providerId, out IEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        return _providers.TryGetValue(providerId, out provider!);
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    private HttpClient CreateClient(EmbeddingProviderOptions entry, Func<EmbeddingProviderOptions, HttpClient>? factory)
    {
        var client = factory?.Invoke(entry) ?? new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, entry.TimeoutSeconds)) };
        _clients.Add(client);
        return client;
    }
}
