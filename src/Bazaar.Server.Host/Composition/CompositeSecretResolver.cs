using Bazaar.Server.Secrets;

namespace Bazaar.Server.Host.Composition;

/// <summary>Tries each registered resolver in order and uses the first that understands the reference's scheme.</summary>
public sealed class CompositeSecretResolver(IEnumerable<ISecretResolver> resolvers) : ISecretResolver
{
    private readonly ISecretResolver[] _resolvers = [.. resolvers];

    public bool CanResolve(string reference) => _resolvers.Any(r => r.CanResolve(reference));

    public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var resolver = _resolvers.FirstOrDefault(r => r.CanResolve(reference))
            ?? throw new SecretResolutionException(reference, "no resolver understands this scheme on this OS.");
        return resolver.ResolveAsync(reference, cancellationToken);
    }
}
