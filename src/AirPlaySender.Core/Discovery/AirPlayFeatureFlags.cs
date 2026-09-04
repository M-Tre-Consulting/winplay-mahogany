using System.Text.RegularExpressions;

namespace AirPlaySender.Core.Discovery;

/// <summary>
/// The AirPlay "features" bitmask (mDNS TXT key <c>features</c> or <c>ft</c>),
/// 64 bits wide. Source: https://emanuelecozzi.net/docs/airplay2/features/,
/// cross-checked against https://openairplay.github.io/airplay-spec/features.html
/// and pyatv's <c>AirPlayFlags</c> (protocols/airplay/utils.py) — both note the
/// two references don't fully agree with each other, which is the nature of a
/// reverse-engineered, never-published bitmask; the handful of bits this
/// project actually branches on (System/CoreUtils pairing, UnifiedMediaControl)
/// are the well-attested ones.
/// </summary>
[Flags]
public enum AirPlayFlags : ulong
{
    None = 0,
    SupportsAirPlayVideoV1 = 1UL << 0,
    SupportsAirPlayPhoto = 1UL << 1,
    SupportsAirPlaySlideShow = 1UL << 5,
    SupportsAirPlayScreen = 1UL << 7,
    SupportsAirPlayAudio = 1UL << 9,
    AudioRedundant = 1UL << 11,
    Authentication_4 = 1UL << 14,
    MetadataFeatures_0 = 1UL << 15,
    MetadataFeatures_1 = 1UL << 16,
    MetadataFeatures_2 = 1UL << 17,
    AudioFormats_0 = 1UL << 18,
    AudioFormats_1 = 1UL << 19,
    AudioFormats_2 = 1UL << 20,
    AudioFormats_3 = 1UL << 21,
    Authentication_1 = 1UL << 23,
    Authentication_8 = 1UL << 26,
    SupportsLegacyPairing = 1UL << 27,
    HasUnifiedAdvertiserInfo = 1UL << 30,
    IsCarPlay = 1UL << 32,
    SupportsAirPlayVideoPlayQueue = 1UL << 33,
    SupportsAirPlayFromCloud = 1UL << 34,
    SupportsTlsPsk = 1UL << 35,
    SupportsUnifiedMediaControl = 1UL << 38,
    SupportsBufferedAudio = 1UL << 40,
    SupportsPtp = 1UL << 41,
    SupportsScreenMultiCodec = 1UL << 42,
    SupportsSystemPairing = 1UL << 43,
    IsApValeriaScreenSender = 1UL << 44,
    SupportsHKPairingAndAccessControl = 1UL << 46,
    SupportsCoreUtilsPairingAndEncryption = 1UL << 48,
    SupportsAirPlayVideoV2 = 1UL << 49,
    MetadataFeatures_3 = 1UL << 50,
    SupportsUnifiedPairSetupAndMFi = 1UL << 51,
    SupportsSetPeersExtendedMessage = 1UL << 52,
    SupportsApSync = 1UL << 54,
    SupportsWoL = 1UL << 55,
    SupportsWoL2 = 1UL << 56,
    SupportsHangdogRemoteControl = 1UL << 58,
    SupportsAudioStreamConnectionSetup = 1UL << 59,
    SupportsAudioMetadataControl = 1UL << 60,
    SupportsRfc2198Redundancy = 1UL << 61,
}

public enum EncryptionType
{
    Unknown = 0,
    Unencrypted = 1,
    Rsa = 2,
    FairPlay = 4,
    MFiSAP = 8,
    FairPlaySAPv25 = 16,
}

/// <summary>
/// Parses the mDNS TXT properties AirPlay/RAOP advertise. Ported from
/// pyatv's <c>protocols/airplay/utils.py</c> (parse_features,
/// is_password_required, get_pairing_requirement) and
/// <c>protocols/raop/parsers.py</c> (get_encryption_types) — MIT licensed,
/// Copyright (c) Pierre Ståhl; see NOTICE.md.
/// </summary>
public static class AirPlayFeatureParser
{
    private const int PinRequiredBit = 0x8;
    private const int PasswordBit = 0x80;
    private const int LegacyPairingBit = 0x200;

    private static readonly Regex FeatureRegex = new(@"^0x([0-9A-Fa-f]{1,8})(?:,0x([0-9A-Fa-f]{1,8})|)$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a features string, e.g. "0x445F8A00" or the two-word form
    /// "0x445F8A00,0x1C340" (low 32 bits, then high 32 bits — the SECOND
    /// comma-separated token is the upper half).
    /// </summary>
    public static AirPlayFlags ParseFeatures(string features)
    {
        Match match = FeatureRegex.Match(features);
        if (!match.Success) return AirPlayFlags.None;
        // Found by code review: combining the two halves via STRING
        // concatenation (high-hex-string + low-hex-string, then parsed as
        // one number) is only correct when the low half happens to be
        // written as a full, zero-padded 8-digit hex string — which every
        // existing test already assumed, so this was never caught. A real
        // device is free to write the low half without leading zeros (e.g.
        // "0x1F0,0x1C340" rather than "0x000001F0,0x1C340"), and string
        // concatenation of an unpadded low half silently shifts the high
        // half down by the missing digits, corrupting the whole bitmask —
        // which every auth-method decision in this project depends on.
        // Parse each half as its own number and combine numerically instead
        // (correct regardless of how many digits either half has).
        ulong low = Convert.ToUInt64(match.Groups[1].Value, 16);
        ulong high = match.Groups[2].Success ? Convert.ToUInt64(match.Groups[2].Value, 16) : 0UL;
        return (AirPlayFlags)((high << 32) | low);
    }

    /// <summary>Reads the "features" TXT property, falling back to "ft" (both names occur in the wild).</summary>
    public static AirPlayFlags GetFeatures(IReadOnlyDictionary<string, string> properties)
    {
        string value = properties.GetValueOrDefault("features") ?? properties.GetValueOrDefault("ft") ?? "0x0";
        return ParseFeatures(value);
    }

    /// <summary>The older, narrower "status flags" field (TXT key "sf", or "flags" on some receivers), a plain hex int.</summary>
    public static int GetStatusFlags(IReadOnlyDictionary<string, string> properties)
    {
        string value = properties.GetValueOrDefault("sf") ?? properties.GetValueOrDefault("flags") ?? "0x0";
        value = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : 0;
    }

    public static EncryptionType GetEncryptionTypes(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("et", out string? et)) return EncryptionType.Unknown;
        EncryptionType result = EncryptionType.Unknown;
        foreach (string part in et.Split(','))
        {
            if (!int.TryParse(part, out int code)) continue;
            result |= code switch
            {
                0 => EncryptionType.Unencrypted,
                1 => EncryptionType.Rsa,
                3 => EncryptionType.FairPlay,
                4 => EncryptionType.MFiSAP,
                5 => EncryptionType.FairPlaySAPv25,
                _ => EncryptionType.Unknown,
            };
        }
        return result;
    }

    public static bool IsPasswordRequired(IReadOnlyDictionary<string, string> properties)
    {
        if (string.Equals(properties.GetValueOrDefault("pw"), "true", StringComparison.OrdinalIgnoreCase)) return true;
        return (GetStatusFlags(properties) & PasswordBit) != 0;
    }

    /// <summary>True if the receiver requires SOME form of pairing (on-screen PIN or transient) before it will accept a connection.</summary>
    public static bool IsPairingMandatory(IReadOnlyDictionary<string, string> properties) =>
        (GetStatusFlags(properties) & (LegacyPairingBit | PinRequiredBit)) != 0;

    /// <summary>True if the receiver advertises PIN-less transient HAP pairing (HomePod, macOS AirPlay Receiver, most AirPlay-2 speakers).</summary>
    public static bool SupportsTransientPairing(IReadOnlyDictionary<string, string> properties)
    {
        AirPlayFlags f = GetFeatures(properties);
        return f.HasFlag(AirPlayFlags.SupportsSystemPairing) || f.HasFlag(AirPlayFlags.SupportsCoreUtilsPairingAndEncryption);
    }

    /// <summary>True if the receiver's "features" indicate AirPlay 2 (vs. legacy AirPlay 1 / bare RAOP).</summary>
    public static bool IsAirPlay2(IReadOnlyDictionary<string, string> properties)
    {
        AirPlayFlags f = GetFeatures(properties);
        return f.HasFlag(AirPlayFlags.SupportsUnifiedMediaControl) || f.HasFlag(AirPlayFlags.SupportsCoreUtilsPairingAndEncryption);
    }
}
