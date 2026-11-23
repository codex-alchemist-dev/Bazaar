using Bazaar.Server.Terminal.Abstractions;
using Bazaar.Server.VmTransport;
using TransportPty = Bazaar.Server.VmTransport.IGuestPty;

namespace Bazaar.Server.Terminal.Vms;

/// <summary>
/// The VM module's half of the terminal seam (docs/design/55, section 3, "Seam"): opens a terminal in an agent's VM over the guest
/// channel the transport holds for it, speaking the guest <c>PtyOpen</c>, <c>PtyData</c>, <c>PtyResize</c>, <c>PtyClose</c> and
/// <c>PtyExit</c> messages (doc 16, section 3) through <see cref="IGuestChannel.OpenPtyAsync"/>.
/// </summary>
/// <remarks>
/// Fails closed. A terminal opens only on a VM that the channel directory serves for that agent: the VM record says it belongs to the
/// agent and is not retired, and its channel is open (the guest agent has connected and its Hello matched the VM). A stopped, saved,
/// retired or unknown VM, another agent's VM, a VM id or agent id that is not a GUID, and a missing directory all refuse. The overload
/// without an agent id refuses always, because it cannot check ownership. The adapter never starts a VM and keeps no state.
/// </remarks>
public sealed class VmGuestPtyChannel : IGuestPtyChannel
{
    private readonly IGuestChannelDirectory? _directory;

    /// <summary>Creates the adapter. A <see langword="null"/> directory means no backend: <see cref="IsAvailable"/> is false.</summary>
    public VmGuestPtyChannel(IGuestChannelDirectory? directory)
    {
        _directory = directory;
    }

    /// <inheritdoc />
    public bool IsAvailable => _directory is not null;

    /// <inheritdoc />
    public Task<Abstractions.IGuestPty> OpenAsync(string vmId, string terminalId, int cols, int rows, CancellationToken ct) =>
        throw new InvalidOperationException("A guest terminal is opened for an agent; the agent id is required to check the VM's owner.");

    /// <inheritdoc />
    public async Task<Abstractions.IGuestPty> OpenForAgentAsync(string agentId, string vmId, string terminalId, int cols, int rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(vmId);
        ct.ThrowIfCancellationRequested();
        if (_directory is null)
        {
            throw new InvalidOperationException("No guest channel directory is registered.");
        }

        if (!Guid.TryParse(vmId, out var vm) || vm == Guid.Empty)
        {
            throw new InvalidOperationException("The VM id is not a GUID.");
        }

        // The directory lists only this agent's live VMs whose channel is open, so a VM of another agent is simply not in the list.
        var guests = await _directory.ListAsync(agentId, ct).ConfigureAwait(false);
        AgentGuest? guest = null;
        foreach (var candidate in guests)
        {
            if (candidate.VmId == vm)
            {
                guest = candidate;
                break;
            }
        }

        if (guest is null)
        {
            throw new InvalidOperationException("The VM is not a running VM of this agent.");
        }

        var pty = await guest.Channel.OpenPtyAsync(cols, rows, ct).ConfigureAwait(false);
        return new GuestPtyAdapter(pty);
    }

    /// <summary>The transport's terminal seen through the terminal module's port. Output order and exit are the transport's own.</summary>
    private sealed class GuestPtyAdapter : Abstractions.IGuestPty
    {
        private readonly TransportPty _inner;

        public GuestPtyAdapter(TransportPty inner)
        {
            _inner = inner;
        }

        public System.Threading.Channels.ChannelReader<byte[]> Output => _inner.Output;

        // The transport completes this with the shell's exit code, or -1 when the host closed the terminal or the channel ended first.
        public Task<int> Exited => _inner.Exited;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => _inner.WriteAsync(bytes, ct);

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => _inner.ResizeAsync(cols, rows, ct);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
