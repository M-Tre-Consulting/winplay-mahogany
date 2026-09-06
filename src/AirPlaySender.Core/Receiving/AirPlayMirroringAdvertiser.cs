using AirPlaySender.Core.Net;
using Makaretu.Dns;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// Advertises this machine as an AirPlay Mirroring target on
/// <c>_airplay._tcp.local.</c> — the first (and cheapest to verify) step of
/// Phase 2 (receiving): before any pairing/video work is worth writing,
/// this alone should be enough for a real iPhone to list "this PC" in
/// Control Center's Screen Mirroring picker.
///
/// The TXT record keys/values below are not guessed — they're the exact
/// set UxPlay (a real, actively maintained open-source AirPlay Mirroring
/// receiver, GPLv3) advertises for `_airplay._tcp`, read from its
/// <c>lib/mdnsd/dnssd_mdnsd.c</c>. Two values are deliberately NOT copied
/// verbatim from UxPlay, on purpose:
///   - <c>pi</c> (the accessory's HAP pairing identifier): UxPlay hardcodes
///     the same GUID for every install of the software. We generate and
///     persist our own per-install (see <see cref="ReceiverIdentity"/>) —
///     the technically correct behavior for something meant to be *this*
///     machine's own distinct identity, not shared across every user who
///     ever built this project.
///   - <c>pk</c>: UxPlay's own Ed25519 long-term public key; ours is
///     <see cref="ReceiverIdentity.PublicKeyHex"/>.
/// <c>model</c> must be a recognized Apple model string — real senders gate
/// mirroring capabilities and, crucially, the H.264 encode bitrate/resolution
/// on it. This project long used UxPlay's <c>AppleTV3,2</c> (Apple TV 3, 2013),
/// which works but makes an iPhone/Mac pick a deliberately conservative,
/// low-bitrate encode. <see cref="Model"/> now defaults to <c>AppleTV6,2</c>
/// (Apple TV 4K, 2017) to test whether identifying as a 4K-class receiver makes
/// a sender offer a sharper, higher-bitrate stream. This is an experiment:
/// revert to <c>AppleTV3,2</c> (or set the <c>WINPLAY_MODEL</c> env var) if a
/// modern model trips a sender into the modern HAP pair-setup path this
/// receiver doesn't fully implement yet.
/// </summary>
public sealed class AirPlayMirroringAdvertiser : IDisposable
{
    private const string AirPlayServiceType = "_airplay._tcp";

    /// <summary>
    /// The Apple model string this receiver claims, in both the mDNS TXT record
    /// and <c>GET /info</c> (one source of truth so they can't disagree).
    /// Overridable at launch with the <c>WINPLAY_MODEL</c> environment variable
    /// so a live test can sweep models without a rebuild — see the class doc
    /// comment for why the default moved off <c>AppleTV3,2</c>.
    /// </summary>
    public static readonly string Model =
        Environment.GetEnvironmentVariable("WINPLAY_MODEL") is { Length: > 0 } m ? m : "AppleTV6,2";

    private readonly ServiceDiscovery _discovery;
    private readonly ServiceProfile _profile;

    public ReceiverIdentity Identity { get; }
    public string InstanceName { get; }
    public string DeviceId { get; }

    /// <param name="instanceName">The name shown in Control Center — defaults to the machine's hostname.</param>
    public AirPlayMirroringAdvertiser(ushort port, ReceiverIdentity? identity = null, string? instanceName = null)
    {
        Identity = identity ?? ReceiverIdentity.LoadOrCreate();
        InstanceName = instanceName ?? Environment.MachineName;
        DeviceId = LocalMachineInfo.MacAddressOrPlaceholder();

        _profile = new ServiceProfile(InstanceName, AirPlayServiceType, port);
        foreach ((string key, string value) in AirPlayTxtRecord.BuildEntries(Identity, DeviceId, Model))
            _profile.AddProperty(key, value);

        // Same VPN/mesh-adapter pitfall as AirPlayDiscovery (see its doc
        // comment) — restrict which interfaces mDNS actually uses instead
        // of letting Windows' interface-metric ordering pick for us.
        _mdns = new MulticastService(_ => CandidateNetworkInterfaces.Get());
        _discovery = new ServiceDiscovery(_mdns);
    }

    private readonly MulticastService _mdns;

    /// <summary>
    /// Passing our own <see cref="MulticastService"/> into <see cref="ServiceDiscovery"/>
    /// (rather than letting it construct one) means it does NOT auto-start it
    /// — confirmed by reading ServiceDiscovery's source, not assumed — so
    /// this has to start it explicitly before advertising.
    /// </summary>
    public void Start()
    {
        _mdns.Start();
        _discovery.Advertise(_profile);
    }

    public void Dispose()
    {
        _discovery.Unadvertise(_profile);
        _discovery.Dispose();
        _mdns.Stop();
    }
}
