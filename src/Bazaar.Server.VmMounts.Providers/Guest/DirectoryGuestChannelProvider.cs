using Bazaar.Server.VmTransport;

namespace Bazaar.Server.VmMounts.Providers.Guest;

/// <summary>
/// The <see cref="IGuestChannelProvider"/> over the transport's channel directory. The directory serves a channel only while it is open and
/// the VM record still says the VM exists, is not retired, and has the registered name and OS, so a stale or foreign channel is never returned.
/// </summary>
public sealed class DirectoryGuestChannelProvider(IGuestChannelDirectory directory) : IGuestChannelProvider
{
    private readonly IGuestChannelDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <inheritdoc />
    public ValueTask<IGuestChannel?> TryGetAsync(Guid vmId, CancellationToken ct) => _directory.TryGetByVmAsync(vmId, ct);
}
