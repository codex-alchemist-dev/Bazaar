using Bazaar.Client.Calcinato.Sources;

namespace Bazaar.Client.Windows;

/// <summary>
/// The wire hub for a head whose server terminal streams are not wired yet. Remote attach fails with a clear message,
/// so only local terminals work until the wire module carries pane streams (doc 55, section 7).
/// </summary>
public sealed class UnavailableRemoteTerminalHub : ITerminalWireHub
{
    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this head does not carry terminal streams yet.</exception>
    public ITerminalWire ForTerminal(string id) => throw new NotSupportedException(
        "Attaching to a server terminal is not available in this build yet. Open a local shell instead.");
}
