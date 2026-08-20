using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Rtsp;
using AirPlaySender.Core.Tlv;

namespace AirPlaySender.Core.Pairing;

/// <summary>
/// HAP pair-verify (X25519 ECDH + Ed25519 sign/verify) — the step that
/// turns a completed pair-setup (or previously stored credentials) into
/// this session's live keys. Only reached on the Apple-TV-style on-screen
/// PIN path; AirPlay-2 transient pairing (HomePod etc.) never runs this,
/// it derives keys straight from the SRP session key at pair-setup M4.
/// </summary>
public static class PairVerifyClient
{
    private const int Hkp = 3;

    public static async Task<PairingResult> RunAsync(RtspConnection conn, PairingIdentity identity, StoredCredentials credentials, CancellationToken ct = default)
    {
        using X25519KeyPair ephemeral = X25519KeyPair.Generate();

        var m1 = new Tlv8.Map();
        m1.Add(Tlv8Type.State, 0x01);
        m1.Add(Tlv8Type.PublicKey, ephemeral.PublicKey);
        RtspResponse resp1 = await conn.SendPairingPostAsync("/pair-verify", Tlv8.Encode(m1), Hkp, ct).ConfigureAwait(false);
        if (!resp1.IsSuccess) throw new PairingRejectedException(resp1.StatusCode, $"Pair-verify was rejected (HTTP {resp1.StatusCode})");

        Tlv8.Map m2 = Tlv8.Decode(resp1.Body);
        PairingTlv.ThrowIfError(m2);
        byte[] sessionPub = PairingTlv.Require(m2, Tlv8Type.PublicKey, "session public key");
        byte[] encryptedM2 = PairingTlv.Require(m2, Tlv8Type.EncryptedData, "M2 encrypted payload");

        byte[]? sharedSecret = ephemeral.Agree(sessionPub);
        if (sharedSecret is null)
            throw new PairingProtocolException(0, "Pair-verify shared-secret derivation failed (malformed or low-order device public key)");

        byte[] verifyKey = Hkdf.DeriveSha512("Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info", sharedSecret, 32);
        byte[]? decryptedM2 = ChaCha20Poly1305Cipher.TryDecrypt(verifyKey, "PV-Msg02"u8.ToArray(), encryptedM2);
        if (decryptedM2 is null) throw new PairingProtocolException(0, "Pair-verify M2 could not be decrypted");

        Tlv8.Map sub = Tlv8.Decode(decryptedM2);
        byte[] accessoryId = PairingTlv.Require(sub, Tlv8Type.Identifier, "accessory identifier");
        byte[] accessorySignature = PairingTlv.Require(sub, Tlv8Type.Signature, "accessory signature");

        // Verify the accessory's signature over sessionPub||accessoryId||ourEphemeralPub against its STORED long-term
        // key. This is the actual anti-impersonation check pair-verify exists for — unlike some reference senders
        // that only warn on a mismatch, this implementation treats it as fatal: a mismatch here means either a
        // spoofed responder or stale/wrong stored credentials, and silently continuing would defeat the point of
        // having pinned the accessory's key at pair-setup time.
        byte[] signedInfo = [.. sessionPub, .. accessoryId, .. ephemeral.PublicKey];
        if (!Ed25519Signer.Verify(credentials.AccessoryLtpk, signedInfo, accessorySignature))
            throw new PairingProtocolException(0,
                "The device's pair-verify signature did not match its stored key. This could mean stale credentials " +
                "(try forgetting and re-pairing the device) or a spoofed responder — refusing to continue either way.");

        byte[] ourDeviceInfo = [.. ephemeral.PublicKey, .. identity.PairingId, .. sessionPub];
        byte[] ourSignature = Ed25519Signer.Sign(identity.Seed32, ourDeviceInfo);
        var innerOut = new Tlv8.Map();
        innerOut.Add(Tlv8Type.Identifier, identity.PairingId);
        innerOut.Add(Tlv8Type.Signature, ourSignature);
        byte[] encryptedOut = ChaCha20Poly1305Cipher.Encrypt(verifyKey, "PV-Msg03"u8.ToArray(), Tlv8.Encode(innerOut));

        var m3 = new Tlv8.Map();
        m3.Add(Tlv8Type.State, 0x03);
        m3.Add(Tlv8Type.EncryptedData, encryptedOut);
        RtspResponse resp3 = await conn.SendPairingPostAsync("/pair-verify", Tlv8.Encode(m3), Hkp, ct).ConfigureAwait(false);
        if (!resp3.IsSuccess) throw new PairingRejectedException(resp3.StatusCode, $"Pair-verify M3 was rejected (HTTP {resp3.StatusCode})");

        return SessionKeyDerivation.Derive(sharedSecret);
    }
}
