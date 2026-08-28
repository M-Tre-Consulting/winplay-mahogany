using System.Net.NetworkInformation;

namespace AirPlaySender.Core.Net;

/// <summary>Small local-machine facts used to identify us to AirPlay peers.</summary>
public static class LocalMachineInfo
{
    /// <summary>
    /// Colon-separated hex MAC of the "primary" adapter (Ethernet preferred
    /// over Wi-Fi, matching the interface-preference already used for
    /// choosing which adapter to run mDNS on). Falls back to a placeholder
    /// if no adapter is up — this is used for device identification, not
    /// crypto, so a placeholder is harmless if genuinely nothing is up.
    /// </summary>
    public static string MacAddressOrPlaceholder()
    {
        try
        {
            NetworkInterface? nic = CandidateNetworkInterfaces.Get()
                .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .FirstOrDefault();
            byte[]? bytes = nic?.GetPhysicalAddress().GetAddressBytes();
            if (bytes is { Length: 6 })
                return string.Join(':', bytes.Select(b => b.ToString("X2")));
        }
        catch { /* best-effort — fall through to the placeholder */ }
        return "AA:BB:CC:DD:EE:FF";
    }
}
