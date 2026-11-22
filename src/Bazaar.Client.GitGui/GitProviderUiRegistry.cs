namespace Bazaar.Client.GitGui;

/// <summary>
/// The provider UIs a head composes, looked up by provider id (doc 52, Client). Ids compare case-insensitively; a duplicate id is
/// rejected at construction, so two provider modules cannot silently shadow each other.
/// </summary>
public sealed class GitProviderUiRegistry : IGitProviderUiRegistry
{
    private readonly Dictionary<string, IGitProviderUi> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ids = [];

    /// <exception cref="ArgumentException">An entry has a blank id, or two entries share an id.</exception>
    public GitProviderUiRegistry(IEnumerable<IGitProviderUi> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.ProviderId))
            {
                throw new ArgumentException("A provider UI has a blank provider id.", nameof(providers));
            }

            if (!_byId.TryAdd(provider.ProviderId, provider))
            {
                throw new ArgumentException($"Provider UI '{provider.ProviderId}' is registered twice.", nameof(providers));
            }

            _ids.Add(provider.ProviderId);
        }
    }

    public IReadOnlyCollection<string> ProviderIds => _ids;

    public IGitProviderUi? Find(string providerId) =>
        providerId is not null && _byId.TryGetValue(providerId, out var provider) ? provider : null;
}
