using AirPlaySender.Core.Tlv;

namespace AirPlaySender.Core.Pairing;

/// <summary>Small helpers shared by <see cref="PairSetupClient"/> and <see cref="PairVerifyClient"/>.</summary>
internal static class PairingTlv
{
    public static void ThrowIfError(Tlv8.Map map)
    {
        byte[]? error = map.Get(Tlv8Type.Error);
        if (error is { Length: > 0 })
            throw new PairingProtocolException(error[0], $"The device rejected pairing (HAP error {error[0]})");
    }

    public static byte[] Require(Tlv8.Map map, Tlv8Type tag, string what)
    {
        byte[]? value = map.Get(tag);
        if (value is null) throw new PairingProtocolException(0, $"Pairing response was missing {what}");
        return value;
    }
}
