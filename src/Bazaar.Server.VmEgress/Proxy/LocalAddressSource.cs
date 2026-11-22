using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bazaar.Server.VmEgress.Proxy;

/// <summary>Supplies the machine's own interface addresses, so the proxy never connects back to the host it runs on.</summary>
internal interface ILocalAddressSource
{
    /// <summary>True when <paramref name="address"/> is one of this machine's unicast addresses. IPv4-mapped IPv6 compares as IPv4.</summary>
    bool IsLocal(IPAddress address);
}

/// <summary>
/// Reads unicast addresses from the network interfaces and caches them for a short time. The snapshot is replaced as a whole, so
/// readers never see a half-built set. If enumeration fails the previous snapshot stays in force.
/// </summary>
internal sealed class SystemLocalAddressSource(TimeSpan? lifetime = null, Func<IEnumerable<IPAddress>>? enumerate = null) : ILocalAddressSource
{
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromSeconds(30);
    private readonly Func<IEnumerable<IPAddress>> _enumerate = enumerate ?? EnumerateInterfaces;
    private readonly Lock _gate = new();
    private HashSet<IPAddress> _snapshot = [];
    private long _expiresAt;
    private bool _loaded;

    public bool IsLocal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Current().Contains(Canonical(address));
    }

    private static IPAddress Canonical(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IEnumerable<IPAddress> EnumerateInterfaces()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                yield return unicast.Address;
            }
        }
    }

    private HashSet<IPAddress> Current()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_loaded && now < _expiresAt)
            {
                return _snapshot;
            }

            try
            {
                var fresh = new HashSet<IPAddress>();
                foreach (var address in _enumerate())
                {
                    var canonical = Canonical(address);
                    if (canonical.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        // A zone id is host-local and would stop an address from comparing equal to the resolved one.
                        canonical = new IPAddress(canonical.GetAddressBytes());
                    }

                    fresh.Add(canonical);
                }

                _snapshot = fresh;
                _loaded = true;
            }
            catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException or PlatformNotSupportedException)
            {
                // Keep the last good snapshot; try again after the lifetime.
            }

            _expiresAt = now + (long)_lifetime.TotalMilliseconds;
            return _snapshot;
        }
    }
}
