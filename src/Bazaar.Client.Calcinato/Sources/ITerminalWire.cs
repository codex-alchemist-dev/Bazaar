using System.Threading.Channels;
using Bazaar.Contracts.Terminal;

namespace Bazaar.Client.Calcinato.Sources;

/// <summary>
/// One terminal's view of the <c>pane:&lt;terminalId&gt;</c> stream. The head or the wire client implements it; this
/// module does not depend on the JSON codec (doc 52, section 6).
/// </summary>
public interface ITerminalWire
{
    /// <summary>Sends one frame to the server.</summary>
    ValueTask SendAsync(TerminalFrame frame, CancellationToken ct);

    /// <summary>Frames from the server, already filtered to this terminal id. Completes when the stream closes.</summary>
    ChannelReader<TerminalFrame> Incoming { get; }
}

/// <summary>Multiplexes the terminal frames of one connection by terminal id.</summary>
public interface ITerminalWireHub
{
    /// <summary>Returns the wire view for one terminal id.</summary>
    ITerminalWire ForTerminal(string id);
}
