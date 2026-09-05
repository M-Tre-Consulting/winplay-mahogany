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

        // This app is itself an _airplay._tcp mirroring receiver (see
        // AirPlayMirroringAdvertiser, started alongside the audio sender) —
        // since fixing the mirroring-only exclusion bug above, our own PC's
        // self-advertisement now passes that filter too and showed up in the
        // sender's own device list ("PC-NICO", found live). Its TXT record
        // carries the exact same "deviceid" this machine's advertiser uses
        // (LocalMachineInfo.MacAddressOrPlaceholder()) — skip anything that matches it.
        string ownDeviceId = Net.LocalMachineInfo.MacAddressOrPlaceholder();

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
            IService? airplay = host.Services.Values.FirstOrDefault(s => s.Name == AirPlayServiceType);
            // A mirroring-only target (a TV with no standalone AirPlay speaker
            // role — confirmed live: a real AirPlay TV advertises ONLY
            // _airplay._tcp, no _raop._tcp at all) has no RAOP endpoint. Only
            // requiring RAOP here silently dropped every such device from
            // discovery — found live, not by inspection.
            if (raop is null && airplay is null) continue;

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (raop is not null) MergeProperties(merged, raop.Properties);
            if (airplay is not null) MergeProperties(merged, airplay.Properties);

            if (merged.TryGetValue("deviceid", out string? seenDeviceId) &&
                string.Equals(seenDeviceId, ownDeviceId, StringComparison.OrdinalIgnoreCase))
                continue; // this is our own PC's mirroring-receiver self-advertisement

            string ip = host.IPAddress ?? host.IPAddresses.FirstOrDefault() ?? "";
            // Prefer RAOP's port (legacy RTSP audio) when both exist — same
            // port choice this method already made before airplay-only devices
            // were reachable at all — else fall back to _airplay._tcp's own.
            int port = raop?.Port > 0 ? raop.Port : airplay?.Port ?? 0;
            if (ip.Length == 0 || port == 0) continue;

            string instanceName = raop?.ServiceName ?? airplay!.ServiceName;
            devices.Add(new AirPlayDevice
            {
                Name = CleanInstanceName(instanceName) is { Length: > 0 } n ? n : host.DisplayName,
                Host = ip,
                Port = port,
                DeviceId = instanceName,
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
    /// Instance names look like "AA:BB:CC:DD:EE:FF@Living Room._raop._tcp.local."
    /// (RAOP) or "AA:BB:CC:DD:EE:FF@Living Room._airplay._tcp.local." (an
    /// airplay-only, e.g. mirroring-only, device) — or, depending on the
    /// resolver, without the trailing service suffix. Strips the MAC-address
    /// prefix and either trailing service-type suffix to get the human-readable
    /// name.
    /// </summary>
    private static string CleanInstanceName(string instanceName)
    {
        int at = instanceName.IndexOf('@');
        string s = at >= 0 ? instanceName[(at + 1)..] : instanceName;
        int suffix = s.IndexOf("._raop", StringComparison.OrdinalIgnoreCase);
        if (suffix < 0) suffix = s.IndexOf("._airplay", StringComparison.OrdinalIgnoreCase);
        return (suffix >= 0 ? s[..suffix] : s).Trim();
    }
}
