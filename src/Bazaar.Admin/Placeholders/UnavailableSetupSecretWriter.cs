using Bazaar.Server.Setup;

namespace Bazaar.Admin.Placeholders;

/// <summary>
/// PLACEHOLDER, fail closed. This build has no secret store, so <c>secret set</c> reports that and stores nothing. The Host adapter
/// replaces this class with one over ISecretStore (doc 14). Its write throws without naming the value, so a caller that skipped the
/// availability check still cannot store or echo anything.
/// </summary>
internal sealed class UnavailableSetupSecretWriter : ISetupSecretWriter
{
    public bool StoreAvailable => false;

    public Task WriteAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("No secret store is registered in this build.");
    }
}
