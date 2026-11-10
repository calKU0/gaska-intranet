using System.Net;
using System.Net.Sockets;

namespace Gaska.Intranet.Core.Security;

public static class NetworkAddress
{
    /// <summary>
    /// "Same network" key for trusted devices. IPv4 – the full address. IPv6 – the /64 prefix, because devices
    /// in the same home/office network regularly change the interface part of the address (privacy extensions).
    /// </summary>
    public static string ToTrustKey(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip))
        {
            return ipAddress;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    public static bool IsSameNetwork(string a, string b) =>
        string.Equals(ToTrustKey(a), ToTrustKey(b), StringComparison.OrdinalIgnoreCase);
}
