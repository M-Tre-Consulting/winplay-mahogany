using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Pairing;

/// <summary>
/// Every AirPlay-2 session key (control channel, event channel) is
/// HKDF-SHA512 over the SAME pairing shared secret, just with a different
/// salt/info label pair — whether that secret came from transient SRP or
/// from X25519 pair-verify. Centralised here so both paths derive
/// identically.
/// </summary>
internal static class SessionKeyDerivation
{
    public static PairingResult Derive(byte[] sharedSecret) => new()
    {
        SharedSecret = sharedSecret,
        ControlWriteKey = Hkdf.DeriveSha512("Control-Salt", "Control-Write-Encryption-Key", sharedSecret, 32),
        ControlReadKey = Hkdf.DeriveSha512("Control-Salt", "Control-Read-Encryption-Key", sharedSecret, 32),
        // Event channel is a REVERSE connection: the accessory "writes" what we read, and "reads" what we write.
        EventReadKey = Hkdf.DeriveSha512("Events-Salt", "Events-Write-Encryption-Key", sharedSecret, 32),
        EventWriteKey = Hkdf.DeriveSha512("Events-Salt", "Events-Read-Encryption-Key", sharedSecret, 32),
    };
}
