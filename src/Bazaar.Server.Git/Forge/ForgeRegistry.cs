namespace Bazaar.Server.Git.Forge;

/// <summary>
/// Resolves the forge provider and its merge executor by provider id (doc 52, Server contract). Ids compare case-insensitively.
/// Two providers (or two executors) claiming one id fail at construction, so a bad composition never starts. An unknown id is a
/// <see cref="UnknownForgeProviderException"/>, never a fallback.
/// </summary>
public sealed class ForgeRegistry : IForgeRegistry
{
    private readonly Dictionary<string, IForgeProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IForgeMergeExecutor> _executors = new(StringComparer.OrdinalIgnoreCase);

    public ForgeRegistry(IEnumerable<IForgeProvider> providers, IEnumerable<IForgeMergeExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(executors);

        foreach (var provider in providers)
        {
            var id = RequireId(provider.ProviderId);
            if (!_providers.TryAdd(id, provider))
                throw new InvalidOperationException($"Two forge providers claim the id '{id}'.");
        }

        foreach (var executor in executors)
        {
            var id = RequireId(executor.ProviderId);
            if (!_executors.TryAdd(id, executor))
                throw new InvalidOperationException($"Two forge merge executors claim the id '{id}'.");
        }
    }

    public IReadOnlyCollection<string> ProviderIds => _providers.Keys;

    public IForgeProvider Resolve(string providerId) =>
        providerId is not null && _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new UnknownForgeProviderException(providerId ?? string.Empty);

    public IForgeMergeExecutor ResolveMergeExecutor(string providerId) =>
        providerId is not null && _executors.TryGetValue(providerId, out var executor)
            ? executor
            : throw new UnknownForgeProviderException(providerId ?? string.Empty);

    private static string RequireId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("A forge provider or executor has an empty provider id.");
        return id;
    }
}
