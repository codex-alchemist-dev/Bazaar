using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Sources;

/// <summary>
/// The head's terminal host. Local terminals use the head's PTY backend when one can run on this OS; remote terminals
/// attach through the wire hub. Without a usable backend, <see cref="SupportsLocal"/> is false and only remote
/// attach works.
/// </summary>
public sealed class TerminalHost : ITerminalHost
{
    private readonly IPtyBackend? _backend;
    private readonly ITerminalWireHub _hub;
    private readonly int _capacity;

    /// <summary>Creates the host.</summary>
    /// <param name="backend">The PTY backend, or null when the head has none.</param>
    /// <param name="hub">Wire hub used for remote attach.</param>
    /// <param name="capacity">Output buffer size for remote sources.</param>
    public TerminalHost(IPtyBackend? backend, ITerminalWireHub hub, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _backend = backend;
        _hub = hub;
        _capacity = capacity;
    }

    /// <inheritdoc />
    public bool SupportsLocal => _backend is { IsSupported: true };

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">No usable PTY backend on this head.</exception>
    public ITerminalSource OpenLocal(TerminalSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!SupportsLocal)
        {
            throw new NotSupportedException("This head has no local terminal backend.");
        }

        return new LocalTerminalSource(_backend!.Open(spec));
    }

    /// <inheritdoc />
    public async Task<ITerminalSource> AttachRemoteAsync(string terminalId, int cols, int rows, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(terminalId);
        var source = new RemoteTerminalSource(_hub.ForTerminal(terminalId), terminalId, _capacity);
        try
        {
            await source.OpenAsync(cols, rows, ct).ConfigureAwait(false);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return source;
    }
}
