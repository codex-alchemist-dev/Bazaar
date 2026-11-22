using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Setup.Host;

/// <summary>The transport rule of the provisioning API is the wire rule: plaintext from off the machine only when <c>wire.allow_plaintext_overlay</c> is set.</summary>
public sealed class WireSetupTransportPolicy(IOptions<WireOptions> options) : ISetupTransportPolicy
{
    /// <inheritdoc />
    public bool AllowPlaintextOverlay => options.Value.AllowPlaintextOverlay;
}
