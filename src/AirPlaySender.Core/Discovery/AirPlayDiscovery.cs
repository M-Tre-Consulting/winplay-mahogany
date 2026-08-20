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
/// </summary>
public sealed class AirPlayDiscovery
{
    private const string RaopServiceType = "_raop._tcp.local.";
    private const string AirPlayServiceType = "_airplay._tcp.local.";

    public async Task<IReadOnlyList<AirPlayDevice>> DiscoverAsync(TimeSpan scanTime, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IZeroconfHost> hosts = await ZeroconfResolver.ResolveAsync(
            [RaopServiceType, AirPlayServiceType],
            scanTime: scanTime,
            cancellationToken: cancellationToken);

        var devices = new List<AirPlayDevice>();
        foreach (IZeroconfHost host in hosts)
        {
            // No RAOP endpoint on this host = nothing we can stream audio to.
            if (!host.Services.TryGetValue(RaopServiceType, out IService? raop)) continue;

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MergeProperties(merged, raop.Properties);
            if (host.Services.TryGetValue(AirPlayServiceType, out IService? airplay))
                MergeProperties(merged, airplay.Properties);

            string ip = host.IPAddress ?? host.IPAddresses.FirstOrDefault() ?? "";
            if (ip.Length == 0 || raop.Port == 0) continue;

            devices.Add(new AirPlayDevice
            {
                Name = CleanInstanceName(raop.Name) is { Length: > 0 } n ? n : host.DisplayName,
                Host = ip,
                Port = raop.Port,
                DeviceId = raop.Name,
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
