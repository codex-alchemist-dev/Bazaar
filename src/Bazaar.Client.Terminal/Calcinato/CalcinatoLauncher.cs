using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Pty;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Calcinato.Sources;

namespace Bazaar.Client.Terminal.Calcinato;

/// <summary>Wires Calcinato to the real console and this OS's PTY backend, and runs it.</summary>
public static class CalcinatoLauncher
{
    /// <summary>Starts a local Calcinato terminal and returns its exit code.</summary>
    /// <param name="options">Parsed <c>--calcinato</c> settings.</param>
    /// <param name="ct">Stops the terminal.</param>
    /// <returns>0 on a normal exit, 1 when no local terminal can run here or the shell could not start.</returns>
    public static async Task<int> RunAsync(CalcinatoOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = new TerminalHost(PtyBackendSelector.CreateDefault(), new NoRemoteTerminalHub());
        if (!host.SupportsLocal)
        {
            Console.Error.WriteLine("Calcinato has no local terminal backend on this system.");
            return 1;
        }

        var manager = new TerminalSessionManager(host);
        await using var calcinato = new CalcinatoHost(new SystemConsoleSurface(), manager);
        try
        {
            return await calcinato.RunAsync(options.Shell, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Calcinato stopped: {ex.Message}");
            return 1;
        }
    }
}

/// <summary>
/// The wire hub for a head with no server connection. Remote attach fails with a clear message, so only local
/// terminals work here.
/// </summary>
public sealed class NoRemoteTerminalHub : ITerminalWireHub
{
    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: this head has no server connection.</exception>
    public ITerminalWire ForTerminal(string id) => throw new NotSupportedException(
        "Attaching to a server terminal needs a server connection. Start the terminal with --server, or open a local shell.");
}
