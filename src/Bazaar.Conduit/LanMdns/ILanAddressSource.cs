using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bazaar.Conduit.LanMdns;

/// <summary>Source of the host's candidate LAN IPv4 addresses. Tests use a fake.</summary>
public interface ILanAddressSource
{
    /// <summary>Returns the addresses to publish, in a stable order, without duplicates.</summary>
    IReadOnlyList<IPAddress> GetLanAddresses();
}

/// <summary>
/// Reads the host's network interfaces. Skips loopback, interfaces that are not up, and IPv4 link-local
/// (169.254.0.0/16) addresses.
/// </summary>
public sealed class SystemLanAddressSource : ILanAddressSource
{
    /// <inheritdoc />
    public IReadOnlyList<IPAddress> GetLanAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork
                    || IPAddress.IsLoopback(address)
                    || IsIPv4LinkLocal(address)
                    || addresses.Contains(address))
                {
                    continue;
                }

                addresses.Add(address);
            }
        }

        return addresses;
    }

    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
