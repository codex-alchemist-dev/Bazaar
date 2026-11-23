using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity;

/// <summary>
/// The default <see cref="IVmActivityReader"/>: it has no source, so it reads nothing and every VM's activity is unknown, which counts
/// as busy. Registered with <c>TryAdd</c>, so a host or module that can really read exec, terminal and browser activity replaces it.
/// </summary>
public sealed class UnreadableVmActivityReader : IVmActivityReader
{
    /// <inheritdoc />
    public Task<VmActivity?> ReadAsync(DensityVm vm, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<VmActivity?>(null);
    }
}
