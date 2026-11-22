using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Bundles;

/// <summary>
/// Signs the exact bytes of <c>manifest.json</c> (docs/design/07-soul-system.md, "Export signing", Q6). The real signer
/// (exporting agent's GitHub App identity plus an operator key) is not built yet; see <see cref="FakeBundleSigner"/> for tests.
/// </summary>
public interface IBundleSigner
{
    /// <summary>Returns a detached signature over <paramref name="manifestBytes"/>.</summary>
    BundleSignature Sign(ReadOnlyMemory<byte> manifestBytes);
}
