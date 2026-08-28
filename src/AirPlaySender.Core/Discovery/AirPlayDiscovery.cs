using System.Net.NetworkInformation;
using AirPlaySender.Core.Net;
using Zeroconf;

namespace AirPlaySender.Core.Discovery;

/// <summary>
/// mDNS/Bonjour discovery of AirPlay/RAOP receivers. Browses
/// <c>_raop._tcp.local.</c> (the actual audio-streaming endpoint — every
/// AirPlay-audio-capable device, HomePod included, advertises this) and
/// <c>_airplay._tcp.local.</c> (carries the richer 64-bit "features" mask
/// and the friendly device name/model on many receivers) together, since
/// Zeroconf groups every service a single responder advertises under one
/// host, and merges their TXT records for the same physical device.
///
/// Explicitly scopes which network interfaces the query goes out on
/// (see <see cref="CandidateNetworkInterfaces"/>) instead of leaving that
/// to Windows' own routing/interface-metric choice. A VPN/mesh adapter (e.g.
/// Tailscale) commonly gets a LOWER interface metric than the real LAN
/// adapter, which is exactly backwards for link-local mDNS multicast — it
/// needs to go out on the interface that's actually on the LAN segment the
/// receiver lives on, not whichever interface Windows currently prefers
/// for general routing. Confirmed empirically: a raw mDNS query bound to
/// the LAN adapter got real replies; letting the OS pick found nothing.
/// </summary>
public sealed class AirPlayDiscovery
{
    private const string RaopServiceType = "_raop._tcp.local.";
    private const string AirPlayServiceType = "_airplay._tcp.local.";

    public async Task<IReadOnlyList<AirPlayDevice>> DiscoverAsync(TimeSpan scanTime, CancellationToken cancellationToken = default)
    {
        NetworkInterface[] candidateInterfaces = CandidateNetworkInterfaces.Get();
        IReadOnlyList<IZeroconfHost> hosts = await ZeroconfResolver.ResolveAsync(
            [RaopServiceType, AirPlayServiceType],
            scanTime: scanTime,
            cancellationToken: cancellationToken,
            netInterfacesToSendRequestOn: candidateInterfaces.Length > 0 ? candidateInterfaces : null);

        var devices = new List<AirPlayDevice>();
        foreach (IZeroconfHost host in hosts)
        {
            // IZeroconfHost.Services is keyed by the per-host SERVICE INSTANCE
            // name (e.g. "F2A6B5611EF6@Sala._raop._tcp.local."), not by the
            // bare service-type string, so matching can't go through a
            // dictionary lookup keyed on the constant we asked to browse for.
            // Confusingly, on IService itself the bare service-type string
            // (what you'd expect from "ServiceName") is actually exposed as
            // `.Name`, while `.ServiceName` is the full per-instance name —
            // verified directly against a live response, not assumed.
            IService? raop = host.Services.Values.FirstOrDefault(s => s.Name == RaopServiceType);
            // No RAOP endpoint on this host = nothing we can stream audio to.
            if (raop is null) continue;

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MergeProperties(merged, raop.Properties);
            IService? airplay = host.Services.Values.FirstOrDefault(s => s.Name == AirPlayServiceType);
            if (airplay is not null)
                MergeProperties(merged, airplay.Properties);

            string ip = host.IPAddress ?? host.IPAddresses.FirstOrDefault() ?? "";
            if (ip.Length == 0 || raop.Port == 0) continue;

            devices.Add(new AirPlayDevice
            {
                Name = CleanInstanceName(raop.ServiceName) is { Length: > 0 } n ? n : host.DisplayName,
                Host = ip,
                Port = raop.Port,
                DeviceId = raop.ServiceName,
                Properties = merged,
            });
        }
        return devices;
    }

    private static void MergeProperties(Dictionary<string, string> into, IReadOnlyList<IReadOnlyDictionary<string, string>> propertySets)
    {
        foreach (IReadOnlyDictionary<string, string> set in propertySets)
            foreach ((string key, string value) in set)
                into[key] = value; // a later TXT record set refines/overrides an earlier one for the same key
    }

    /// <summary>
    /// RAOP instance names look like "AA:BB:CC:DD:EE:FF@Living Room._raop._tcp.local."
    /// (or, depending on the resolver, without the trailing service suffix).
    /// Strips the MAC-address prefix and any trailing service-type suffix to
    /// get the human-readable name.
    /// </summary>
    private static string CleanInstanceName(string raopInstanceName)
    {
        int at = raopInstanceName.IndexOf('@');
        string s = at >= 0 ? raopInstanceName[(at + 1)..] : raopInstanceName;
        int suffix = s.IndexOf("._raop", StringComparison.OrdinalIgnoreCase);
        return (suffix >= 0 ? s[..suffix] : s).Trim();
    }
}
