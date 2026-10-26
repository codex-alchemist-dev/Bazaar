using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Tests.Wire.Harness;

/// <summary>A request handler written as a lambda.</summary>
public sealed class DelegateHandler(string type, Func<WireRequestContext, CancellationToken, ValueTask<WireReply>> handle) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => type;

    /// <inheritdoc />
    public ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken) => handle(context, cancellationToken);
}
