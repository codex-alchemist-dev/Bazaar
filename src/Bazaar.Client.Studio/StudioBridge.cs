using Bazaar.Client.Studio.Abstractions;

namespace Bazaar.Client.Studio;

/// <summary>
/// Composes the Studio bridge. The repo has no client DI convention yet, so a head (Bazaar.Client.Windows) calls this factory
/// with the configured Studio MCP server command and a server-backed grant verifier.
/// </summary>
public static class StudioBridge
{
    /// <summary>Creates the relay and the stdio transport it owns; dispose the transport on shutdown.</summary>
    public static (IStudioRelay Relay, IStudioMcpTransport Transport) Create(
        StdioMcpServerCommand command,
        IStudioGrantVerifier verifier,
        StudioRelayOptions? options = null)
    {
        var transport = new StdioMcpTransport(command);
        return (new StudioRelay(transport, verifier, options), transport);
    }
}
