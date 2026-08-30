using System.Text;
using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Pairing;
using AirPlaySender.Core.Tlv;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The GENUINE HAP TLV8 pair-verify, accessory side — the scheme a modern
/// iOS client actually uses for AirPlay Mirroring against a real receiver
/// (confirmed byte-for-byte from a real capture: iPhone → a real Hisense
/// TV, decoded with a Mac's <c>rvictl</c>/<c>tcpdump</c> since the session
/// runs over AWDL, invisible to any capture taken on the Wi-Fi
/// infrastructure side). This supersedes <see cref="PairingAccessorySession"/>
/// (the legacy byte-offset/AES-CTR scheme) for clients recent enough to
/// negotiate this path — which one a given client uses is decided upstream
/// of pair-verify (mDNS TXT / <c>GET /info</c> capability signalling, not
/// yet fully characterized), so both classes coexist in this codebase for now.
///
/// This is the EXACT mirror image of this project's own
/// <see cref="Pairing.PairVerifyClient"/> (Phase 1, controller role) — same
/// TLV8 tags, same HKDF salt/info strings, same ChaCha20-Poly1305 nonce
/// labels ("PV-Msg02"/"PV-Msg03") — just with the two roles' steps swapped,
/// and validated exactly that way: <c>HapPairVerifyAccessorySessionTests</c>
/// runs this class against the real <see cref="Pairing.PairVerifyClient"/>
/// end-to-end (same trick as this project's own <c>FakeAirPlay2Receiver</c>
/// test double, just the other pairing direction), not merely mirrored by
/// eye. Two real differences confirmed from the capture, not guessed:
///  - the real client's M1 also carries a <c>Method</c> TLV (value 7) that
///    <see cref="Pairing.PairVerifyClient"/>'s own M1 never sends — accepted
///    here but not required or acted on, since nothing about the rest of the
///    exchange depends on it and rejecting a field we don't understand yet
///    would be pure self-sabotage;
///  - the real client sent <c>X-Apple-HKP: 6</c> on these requests, not the
///    3/4 this project's own Phase 1 code uses for AirPlay-2 audio pairing —
///    a header value <see cref="AirPlayReceiverServer"/> doesn't currently
///    inspect at all (dispatch is by URL only), so it's not yet acted on,
///    only recorded here for whoever wires this in next.
///
/// What THIS class deliberately does not solve: pair-setup. The captured
/// session skipped it entirely (the phone was already paired with that TV
/// from an earlier session), so there is no real-traffic evidence yet for
/// what mirroring's own pair-setup looks like — whether it's the transient
/// (HomePod-style, no identity exchange) or PIN-identity (Apple-TV-style)
/// shape this project already has full reference code for on the controller
/// side, run under this same unfamiliar HKP value, or something else
/// again. Guessing which and building it now would repeat the exact mistake
/// this whole investigation has been careful to avoid all night: presenting
/// an unverified guess as if it were a verified fact. <see cref="_clientLtpk"/>
/// is therefore accepted as a constructor parameter — the caller's
/// responsibility, once pair-setup exists, to supply what it learned and
/// persisted there.
/// </summary>
public sealed class HapPairVerifyAccessorySession
{
    private readonly ReceiverIdentity _identity;
    private readonly byte[] _clientLtpk;

    private X25519KeyPair? _ourEphemeral;
    private byte[]? _clientEphemeralPublic;
    private byte[]? _verifyKey;
    private byte[]? _sharedSecret;

    public bool IsVerified { get; private set; }

    /// <summary>The pair-verify ECDH shared secret, available once <see cref="IsVerified"/> is true — feeds <see cref="SessionKeyDerivation"/> for the encrypted-channel keys.</summary>
    public byte[]? SharedSecret => IsVerified ? _sharedSecret : null;

    public HapPairVerifyAccessorySession(ReceiverIdentity identity, byte[] clientLtpk)
    {
        _identity = identity;
        _clientLtpk = clientLtpk;
    }

    /// <summary>Returns the TLV8 response body, or null to signal "reject — close the connection" (malformed request or a signature that doesn't verify).</summary>
    public byte[]? Handle(byte[] body)
    {
        Tlv8.Map req = Tlv8.Decode(body);
        byte[]? state = req.Get(Tlv8Type.State);
        if (state is not { Length: 1 }) return null;
        return state[0] switch
        {
            0x01 => HandleM1(req),
            0x03 => HandleM3(req),
            _ => null,
        };
    }

    private byte[]? HandleM1(Tlv8.Map req)
    {
        byte[]? clientPub = req.Get(Tlv8Type.PublicKey);
        if (clientPub is not { Length: 32 }) return null;
        _clientEphemeralPublic = clientPub;

        _ourEphemeral = X25519KeyPair.Generate();
        _sharedSecret = _ourEphemeral.Agree(clientPub);
        if (_sharedSecret is null) return null; // malformed/low-order peer key

        _verifyKey = Hkdf.DeriveSha512("Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info", _sharedSecret, 32);

        byte[] accessoryId = Encoding.UTF8.GetBytes(_identity.Pi.ToString("D").ToUpperInvariant());
        byte[] signedInfo = [.. _ourEphemeral.PublicKey, .. accessoryId, .. clientPub];
        byte[] signature = Ed25519Signer.Sign(_identity.Seed32, signedInfo);

        var inner = new Tlv8.Map();
        inner.Add(Tlv8Type.Identifier, accessoryId);
        inner.Add(Tlv8Type.Signature, signature);
        byte[] encrypted = ChaCha20Poly1305Cipher.Encrypt(_verifyKey, "PV-Msg02"u8.ToArray(), Tlv8.Encode(inner));

        var m2 = new Tlv8.Map();
        m2.Add(Tlv8Type.State, 0x02);
        m2.Add(Tlv8Type.PublicKey, _ourEphemeral.PublicKey);
        m2.Add(Tlv8Type.EncryptedData, encrypted);
        return Tlv8.Encode(m2);
    }

    private byte[]? HandleM3(Tlv8.Map req)
    {
        if (_verifyKey is null || _clientEphemeralPublic is null || _ourEphemeral is null) return null;
        byte[]? encryptedIn = req.Get(Tlv8Type.EncryptedData);
        if (encryptedIn is null) return null;

        byte[]? decrypted = ChaCha20Poly1305Cipher.TryDecrypt(_verifyKey, "PV-Msg03"u8.ToArray(), encryptedIn);
        if (decrypted is null) return null;

        Tlv8.Map sub = Tlv8.Decode(decrypted);
        byte[]? clientId = sub.Get(Tlv8Type.Identifier);
        byte[]? clientSignature = sub.Get(Tlv8Type.Signature);
        if (clientId is null || clientSignature is null) return null;

        byte[] signedInfo = [.. _clientEphemeralPublic, .. clientId, .. _ourEphemeral.PublicKey];
        if (!Ed25519Signer.Verify(_clientLtpk, signedInfo, clientSignature)) return null;

        IsVerified = true;
        var m4 = new Tlv8.Map();
        m4.Add(Tlv8Type.State, 0x04);
        return Tlv8.Encode(m4);
    }
}
