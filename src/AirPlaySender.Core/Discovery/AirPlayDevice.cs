namespace AirPlaySender.Core.Discovery;

/// <summary>How a receiver wants to be authenticated before it will accept a stream.</summary>
public enum AirPlayAuthMethod
{
    /// <summary>Open RAOP, no auth at all (very old / LAN-only devices).</summary>
    None,
    /// <summary>RTSP digest auth (TXT "pw=true").</summary>
    Password,
    /// <summary>One-shot unauthenticated POST /auth-setup (old AirPort Express gen 2, MFi-SAP).</summary>
    AuthSetupMfiSap,
    /// <summary>Fixed-PIN ("3939") transient HAP pairing — HomePod, macOS AirPlay Receiver, most AirPlay 2 speakers. No user interaction.</summary>
    HapTransient,
    /// <summary>On-screen 4-digit PIN HAP pairing (Apple TV) the first time; pair-verify only on later connects once credentials are stored.</summary>
    HapPin,
}

/// <summary>A discovered AirPlay/RAOP receiver, with enough mDNS TXT data to decide how to authenticate to it.</summary>
public sealed class AirPlayDevice
{
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    /// <summary>Stable per-device id (the RAOP instance name, normally "AA:BB:CC:DD:EE:FF@Name"), used as the credential-store key.</summary>
    public required string DeviceId { get; init; }
    public required IReadOnlyDictionary<string, string> Properties { get; init; }

    public string Model => Properties.GetValueOrDefault("am", "");
    public bool RequiresPassword => AirPlayFeatureParser.IsPasswordRequired(Properties);
    public bool IsAirPlay2 => AirPlayFeatureParser.IsAirPlay2(Properties);

    /// <summary>
    /// Decides which pairing/auth path to use, mirroring pyatv's
    /// extract_credentials + get_pairing_requirement: transient pairing
    /// wins whenever the receiver advertises it (no user interaction
    /// needed); otherwise fall back to on-screen-PIN HAP pairing if the
    /// receiver's status flags mark pairing mandatory; otherwise the older
    /// MFi-SAP/password/open paths.
    /// </summary>
    public AirPlayAuthMethod DetermineAuthMethod()
    {
        if (AirPlayFeatureParser.SupportsTransientPairing(Properties)) return AirPlayAuthMethod.HapTransient;
        if (AirPlayFeatureParser.IsPairingMandatory(Properties)) return AirPlayAuthMethod.HapPin;
        if (AirPlayFeatureParser.GetEncryptionTypes(Properties).HasFlag(EncryptionType.MFiSAP)) return AirPlayAuthMethod.AuthSetupMfiSap;
        if (RequiresPassword) return AirPlayAuthMethod.Password;
        return AirPlayAuthMethod.None;
    }
}
