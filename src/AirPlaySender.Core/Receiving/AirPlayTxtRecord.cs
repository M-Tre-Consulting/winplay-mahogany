using System.Text;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The <c>_airplay._tcp</c> TXT record content, as one shared source of
/// truth: it goes out twice, in two different encodings, and they must
/// describe the same accessory or a real iPhone gets confused —
/// <see cref="AirPlayMirroringAdvertiser"/> hands the key/value pairs to
/// Makaretu.Dns for the mDNS TXT record, and <see cref="AirPlayReceiverServer"/>
/// echoes the same content, wire-encoded, inside the very first
/// <c>GET /info</c> response body (the "txtAirPlay" qualifier — see the
/// doc comment on <see cref="AirPlayMirroringAdvertiser"/> for where these
/// exact keys/values come from).
/// </summary>
public static class AirPlayTxtRecord
{
    public static IReadOnlyList<(string Key, string Value)> BuildEntries(ReceiverIdentity identity, string deviceId, string model) =>
    [
        ("deviceid", deviceId),
        ("features", "0x5A7FFEE6,0x0"),
        ("pw", "false"),
        ("flags", "0x4"),
        ("model", model),
        ("pk", identity.PublicKeyHex),
        ("pi", identity.Pi.ToString()),
        ("srcvers", "220.68"),
        ("vv", "2"),
    ];

    /// <summary>Classic DNS TXT wire format: repeated <c>[1-byte length][ASCII "key=value"]</c>, concatenated.</summary>
    public static byte[] EncodeWire(IReadOnlyList<(string Key, string Value)> entries)
    {
        using var ms = new MemoryStream();
        foreach ((string key, string value) in entries)
        {
            byte[] bytes = Encoding.ASCII.GetBytes($"{key}={value}");
            ms.WriteByte(checked((byte)bytes.Length));
            ms.Write(bytes);
        }
        return ms.ToArray();
    }
}
