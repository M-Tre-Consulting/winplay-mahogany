namespace AirPlaySender.Core.Pairing;

/// <summary>
/// The live session keys a completed pairing (transient pair-setup, or
/// pair-setup+pair-verify) hands to the RTSP/event/audio layers. Field
/// names describe what WE do with each key, not the HKDF info-string it
/// came from — the event-channel pair is a REVERSE connection, so its
/// "write"/"read" HKDF labels are swapped relative to our usage; naming
/// fields by our own usage avoids re-deriving that swap at every call site.
/// </summary>
public sealed class PairingResult
{
    /// <summary>Raw pairing shared secret: SRP session key K (64 bytes, transient) or the X25519 ECDH output (32 bytes, pair-verify). The AirPlay-2 audio key is the first 32 bytes of THIS, used directly with no further HKDF.</summary>
    public required byte[] SharedSecret { get; init; }

    /// <summary>Encrypts what we send on the RTSP control channel (HKDF "Control-Write-Encryption-Key").</summary>
    public required byte[] ControlWriteKey { get; init; }
    /// <summary>Decrypts what the receiver sends back on the RTSP control channel (HKDF "Control-Read-Encryption-Key").</summary>
    public required byte[] ControlReadKey { get; init; }

    /// <summary>Decrypts what the receiver pushes on the event channel (HKDF "Events-Write-Encryption-Key" — named from the accessory's point of view).</summary>
    public required byte[] EventReadKey { get; init; }
    /// <summary>Encrypts our replies on the event channel (HKDF "Events-Read-Encryption-Key" — named from the accessory's point of view).</summary>
    public required byte[] EventWriteKey { get; init; }

    /// <summary>The AirPlay-2 realtime audio key: SharedSecret truncated to 32 bytes, used directly with ChaCha20-Poly1305 (no HKDF — see AirPlaySession/AudioEncryptor for why).</summary>
    public byte[] AudioKey => SharedSecret.Length > 32 ? SharedSecret[..32] : SharedSecret;
}

/// <summary>The receiver refused this pairing mode at the HTTP layer (403/470); the caller should try a different mode, or stop with a clear message.</summary>
public sealed class PairingRejectedException(int httpStatusCode, string message) : Exception(message)
{
    public int HttpStatusCode { get; } = httpStatusCode;
}

/// <summary>A HAP protocol-level error TLV (kTLVType_Error) came back from the receiver.</summary>
public sealed class PairingProtocolException(byte errorCode, string message) : Exception(message)
{
    public byte ErrorCode { get; } = errorCode;
}
