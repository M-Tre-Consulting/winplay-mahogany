using System.Text;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The accessory (receiver) side of AirPlay Mirroring's pairing handshake
/// for one TCP connection — deliberately NOT the HAP TLV8/SRP scheme this
/// project already implements for AirPlay-2 audio (<c>Pairing/</c>,
/// controller side). A real iPhone tapping this PC for mirroring (no PIN,
/// matching this project's advertised <c>pw=false</c>) uses an older,
/// simpler scheme with raw byte offsets instead of TLV8 and AES-128-CTR
/// instead of ChaCha20-Poly1305 — confirmed by reading UxPlay's
/// <c>lib/pairing.c</c> (a real, working receiver), not assumed, after the
/// first attempt at guessing this from the HAP scheme failed against a
/// real iPhone (see the commit history / README for that story).
///
/// <b>pair-setup</b>: the client's 32-byte body is unused — UxPlay's own
/// handler doesn't even read it for this (un-PIN'd) flow — we just answer
/// with our long-term Ed25519 public key, raw.
///
/// <b>pair-verify</b>, two POSTs on the same connection, dispatched on the
/// first body byte:
///  - step 1 (byte 0 = 1): body is <c>[4-byte pad][32B client X25519 ephemeral pk][32B client Ed25519 long-term pk]</c>.
///    We generate our own X25519 ephemeral pair, ECDH with the client's,
///    sign <c>ourPk||theirPk</c> with our long-term Ed25519 key, and
///    AES-128-CTR-encrypt that signature with a key/iv derived from the
///    ECDH secret. Reply: <c>ourPk(32) || encryptedSignature(64)</c>.
///  - step 2 (byte 0 = 0): body is <c>[4-byte pad][64B encrypted signature]</c>,
///    the client's proof, over <c>theirPk||ourPk</c> — note the reversed
///    order vs. step 1 — signed with the Ed25519 key it gave us in step 1.
///    Decrypting it needs the keystream to pick up where step 1's
///    encryption left off (4 blocks in) — both directions share one
///    logical AES-CTR stream from the same derived key/iv, confirmed
///    against the reference's own "fake round" comment, not guessed.
///
/// The RTSP connection itself stays plaintext after this succeeds — unlike
/// AirPlay-2 audio pairing, nothing here wraps the rest of the channel in
/// HAP frames. (Checked: the reference has no such step for mirroring;
/// confidentiality for the actual video comes later, from the separate
/// FairPlay/AES exchange on <c>/fp-setup</c>.)
/// </summary>
public sealed class PairingAccessorySession
{
    private readonly ReceiverIdentity _identity;

    private byte[]? _ourEphemeralPublic;
    private byte[]? _ecdhSecret;
    private byte[]? _clientX25519Public;
    private byte[]? _clientEd25519Public;

    public bool IsVerified { get; private set; }

    public PairingAccessorySession(ReceiverIdentity identity) => _identity = identity;

    public byte[] HandlePairSetup(byte[] requestBody) => _identity.PublicKey32;

    /// <summary>
    /// The pair-verify ECDH shared secret, available once <see cref="IsVerified"/>
    /// is true — SETUP needs it to re-hash the FairPlay-decrypted per-session
    /// AES key (see UxPlay's raop_handlers.h: "aeskey must now be hashed
    /// with it" when legacy pairing set up a shared secret).
    /// </summary>
    public byte[]? EcdhSecret => IsVerified ? _ecdhSecret : null;

    /// <summary>
    /// Event-channel keys, mirroring <c>Pairing/SessionKeyDerivation.cs</c>
    /// (built for the OTHER pairing flow, AirPlay-2 audio's HAP TLV8/SRP) —
    /// same "Events-Salt" HKDF-SHA512 convention, tried here on the theory
    /// that Apple reuses it across the AirPlay family since the underlying
    /// X25519 ECDH secret is the same *kind* of value either way. Unverified
    /// against a reference for this specific (legacy/mirroring) pairing —
    /// this is past what any available source documents.
    /// </summary>
    public (byte[] WriteKey, byte[] ReadKey)? EventChannelKeys => EcdhSecret is not { } secret ? null : (
        Hkdf.DeriveSha512("Events-Salt", "Events-Write-Encryption-Key", secret, 32),
        Hkdf.DeriveSha512("Events-Salt", "Events-Read-Encryption-Key", secret, 32));

    /// <summary>Returns the response body, or null to signal "reject — close the connection" (malformed request or a signature that doesn't verify).</summary>
    public byte[]? HandlePairVerify(byte[] body)
    {
        if (body.Length < 4) return null;
        return body[0] switch
        {
            1 => HandleStep1(body),
            0 => HandleStep2(body),
            _ => null,
        };
    }

    private byte[]? HandleStep1(byte[] body)
    {
        if (body.Length != 4 + 32 + 32) return null;
        _clientX25519Public = body[4..36];
        _clientEd25519Public = body[36..68];

        using X25519KeyPair ours = X25519KeyPair.Generate();
        _ourEphemeralPublic = ours.PublicKey;
        _ecdhSecret = ours.Agree(_clientX25519Public);
        if (_ecdhSecret is null) return null; // malformed/low-order peer key

        byte[] sigMsg = [.. _ourEphemeralPublic, .. _clientX25519Public];
        byte[] signature = Ed25519Signer.Sign(_identity.Seed32, sigMsg);
        (byte[] key, byte[] iv) = DeriveAesKeyIv();
        byte[] encryptedSignature = AesCtrCipher.Transform(key, iv, blockOffset: 0, signature);

        return [.. _ourEphemeralPublic, .. encryptedSignature];
    }

    private byte[]? HandleStep2(byte[] body)
    {
        if (body.Length != 4 + 64 || _ecdhSecret is null || _ourEphemeralPublic is null
            || _clientX25519Public is null || _clientEd25519Public is null)
            return null;

        (byte[] key, byte[] iv) = DeriveAesKeyIv();
        // 4-block skip: step 1 already consumed the first 64 bytes (4
        // blocks) of this same derived keystream encrypting OUR signature.
        byte[] signature = AesCtrCipher.Transform(key, iv, blockOffset: 4, body.AsSpan(4));

        byte[] sigMsg = [.. _clientX25519Public, .. _ourEphemeralPublic]; // reversed vs. step 1's sigMsg
        if (!Ed25519Signer.Verify(_clientEd25519Public, sigMsg, signature)) return null;

        IsVerified = true;
        return [];
    }

    private (byte[] key, byte[] iv) DeriveAesKeyIv()
    {
        byte[] key = Sha.Sha512([.. Encoding.ASCII.GetBytes("Pair-Verify-AES-Key"), .. _ecdhSecret!])[..16];
        byte[] iv = Sha.Sha512([.. Encoding.ASCII.GetBytes("Pair-Verify-AES-IV"), .. _ecdhSecret!])[..16];
        return (key, iv);
    }
}
