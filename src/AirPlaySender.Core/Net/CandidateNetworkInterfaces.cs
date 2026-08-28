using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirPlaySender.Core.Net;

/// <summary>
/// Interfaces worth sending/listening for mDNS traffic on: up, not
/// loopback, and carrying at least one real (non link-local, non-APIPA)
/// IPv4 unicast address — i.e. actually configured on a LAN, not a
/// VPN/tunnel adapter sitting at a 169.254.x.x fallback address or a
/// disconnected Wi-Fi/Ethernet adapter Windows still lists as present.
///
/// Shared by <see cref="Discovery.AirPlayDiscovery"/> (querying) and
/// <see cref="Receiving.AirPlayMirroringAdvertiser"/> (advertising): a
/// VPN/mesh adapter (e.g. Tailscale) commonly gets a LOWER interface metric
/// than the real LAN adapter, which is exactly backwards for link-local
/// mDNS multicast — confirmed empirically during discovery debugging (a
/// raw mDNS query bound to the LAN adapter got real replies; letting the
/// OS pick found nothing), and there is no reason advertising would be
/// immune to the same problem.
/// </summary>
public static class CandidateNetworkInterfaces
{
    public static NetworkInterface[] Get()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                       && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                       && HasRealIPv4Address(nic))
            .ToArray();
    }

    private static bool HasRealIPv4Address(NetworkInterface nic) =>
        nic.GetIPProperties().UnicastAddresses.Any(a =>
            a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !IsLinkLocal(a.Address));

    private static bool IsLinkLocal(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254; // 169.254.0.0/16 (APIPA / unconfigured)
    }
}
