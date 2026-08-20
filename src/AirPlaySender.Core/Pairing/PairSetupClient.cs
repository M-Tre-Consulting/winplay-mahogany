using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Rtsp;
using AirPlaySender.Core.Tlv;

namespace AirPlaySender.Core.Pairing;

/// <summary>
/// HAP pair-setup (SRP-6a) over the RTSP connection's HTTP POST framing.
/// Two shapes, per the wire recipe:
///
///  • <see cref="RunTransientAsync"/> — HomePod / macOS AirPlay Receiver /
///    most AirPlay-2 speakers. Username is always "Pair-Setup", password is
///    the FIXED pin "3939" (no user interaction). Stops at M4 — no
///    identity exchange, no persisted credentials — and hands back the
///    session keys directly (the SRP session key IS the pairing secret
///    for a transient session).
///
///  • <see cref="RunPinAsync"/> — Apple TV, the first time. Runs the full
///    M1..M6 identity exchange with the on-screen 4-digit PIN and returns
///    the long-term <see cref="StoredCredentials"/> to persist; the caller
///    must still run <see cref="PairVerifyClient"/> afterwards (with those
///    credentials) to get live session keys — pair-setup alone never
///    produces them for this path.
/// </summary>
public static class PairSetupClient
{
    private const string TransientPin = "3939";

    public static async Task<PairingResult> RunTransientAsync(RtspConnection conn, CancellationToken ct = default)
    {
        const int hkp = 4; // X-Apple-HKP: 4 selects the transient path

        var m1 = new Tlv8.Map();
        m1.Add(Tlv8Type.Method, 0x00);
        m1.Add(Tlv8Type.State, 0x01);
        m1.Add(Tlv8Type.Flags, [0x10]); // kPairingFlag_Transient — ONE byte on the wire, not the 4-byte HAP-spec width (verified against real hardware)
        Tlv8.Map m2 = await PostAsync(conn, "/pair-setup", m1, hkp, ct).ConfigureAwait(false);

        byte[] salt = Require(m2, Tlv8Type.Salt, "salt");
        byte[] serverB = Require(m2, Tlv8Type.PublicKey, "SRP public value B");

        var srp = new Srp6aClient();
        srp.Start(TransientPin);
        if (!srp.Process(salt, serverB))
            throw new PairingProtocolException(0, "The device sent an invalid SRP public value (B ≡ 0 mod N)");

        var m3 = new Tlv8.Map();
        m3.Add(Tlv8Type.State, 0x03);
        m3.Add(Tlv8Type.PublicKey, srp.PublicA);
        m3.Add(Tlv8Type.Proof, srp.ProofM1);
        Tlv8.Map m4 = await PostAsync(conn, "/pair-setup", m3, hkp, ct).ConfigureAwait(false);

        // Server proof mismatch is logged, not fatal, upstream (matches the
        // reference: a receiver that already accepted our M3 proof is
        // trusted for the session either way).
        m4.Get(Tlv8Type.Proof); // present but intentionally unchecked here — see PairVerifyClient for the path that DOES hard-fail on a signature mismatch

        return SessionKeyDerivation.Derive(srp.SessionKey);
    }

    public static async Task<StoredCredentials> RunPinAsync(RtspConnection conn, PairingIdentity identity, Func<Task<string>> requestPin, CancellationToken ct = default)
    {
        const int hkp = 3;

        // A tvOS Apple TV only RENDERS its 4-digit code once it receives this;
        // /pair-setup M1 alone returns SRP material without displaying anything.
        await SendPinStartAsync(conn, hkp, ct).ConfigureAwait(false);

        var m1 = new Tlv8.Map();
        m1.Add(Tlv8Type.Method, 0x00);
        m1.Add(Tlv8Type.State, 0x01);
        Tlv8.Map m2 = await PostAsync(conn, "/pair-setup", m1, hkp, ct).ConfigureAwait(false);
        byte[] salt = Require(m2, Tlv8Type.Salt, "salt");
        byte[] serverB = Require(m2, Tlv8Type.PublicKey, "SRP public value B");

        string pin = await requestPin().ConfigureAwait(false);

        var srp = new Srp6aClient();
        srp.Start(pin);
        if (!srp.Process(salt, serverB))
            throw new PairingProtocolException(0, "The device sent an invalid SRP public value (B ≡ 0 mod N)");

        var m3 = new Tlv8.Map();
        m3.Add(Tlv8Type.State, 0x03);
        m3.Add(Tlv8Type.PublicKey, srp.PublicA);
        m3.Add(Tlv8Type.Proof, srp.ProofM1);
        Tlv8.Map m4 = await PostAsync(conn, "/pair-setup", m3, hkp, ct).ConfigureAwait(false);
        byte[]? serverProof = m4.Get(Tlv8Type.Proof);
        if (serverProof is not null && !srp.VerifyServerProof(serverProof))
            throw new PairingProtocolException(0, "The PIN was accepted by SRP but the device's proof did not verify — refusing to continue");

        // M5: prove our long-term identity to the accessory.
        byte[] sessionKey = Hkdf.DeriveSha512("Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info", srp.SessionKey, 32);
        byte[] controllerSignSalt = Hkdf.DeriveSha512("Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info", srp.SessionKey, 32);
        byte[] deviceInfo = [.. controllerSignSalt, .. identity.PairingId, .. identity.PublicKey32];
        byte[] signature = Ed25519Signer.Sign(identity.Seed32, deviceInfo);

        var inner = new Tlv8.Map();
        inner.Add(Tlv8Type.Identifier, identity.PairingId);
        inner.Add(Tlv8Type.PublicKey, identity.PublicKey32);
        inner.Add(Tlv8Type.Signature, signature);
        byte[] encryptedM5 = ChaCha20Poly1305Cipher.Encrypt(sessionKey, "PS-Msg05"u8.ToArray(), Tlv8.Encode(inner));

        var m5 = new Tlv8.Map();
        m5.Add(Tlv8Type.State, 0x05);
        m5.Add(Tlv8Type.EncryptedData, encryptedM5);
        Tlv8.Map m6 = await PostAsync(conn, "/pair-setup", m5, hkp, ct).ConfigureAwait(false);

        byte[] encryptedM6 = Require(m6, Tlv8Type.EncryptedData, "M6 encrypted payload");
        byte[]? decryptedM6 = ChaCha20Poly1305Cipher.TryDecrypt(sessionKey, "PS-Msg06"u8.ToArray(), encryptedM6);
        if (decryptedM6 is null) throw new PairingProtocolException(0, "Pairing M6 could not be decrypted");

        Tlv8.Map sub = Tlv8.Decode(decryptedM6);
        byte[] accessoryId = Require(sub, Tlv8Type.Identifier, "accessory identifier");
        byte[] accessoryLtpk = Require(sub, Tlv8Type.PublicKey, "accessory long-term public key");

        return new StoredCredentials
        {
            LtSeed = identity.Seed32,
            PairingId = identity.PairingId,
            AccessoryId = accessoryId,
            AccessoryLtpk = accessoryLtpk,
        };
    }

    private static async Task SendPinStartAsync(RtspConnection conn, int hkp, CancellationToken ct)
    {
        RtspResponse response = await conn.SendPairingPostAsync("/pair-pin-start", [], hkp, ct).ConfigureAwait(false);
        if (!response.IsSuccess) throw new PairingRejectedException(response.StatusCode, DescribeRejection(response.StatusCode));
    }

    private static async Task<Tlv8.Map> PostAsync(RtspConnection conn, string uri, Tlv8.Map body, int hkp, CancellationToken ct)
    {
        RtspResponse response = await conn.SendPairingPostAsync(uri, Tlv8.Encode(body), hkp, ct).ConfigureAwait(false);
        if (!response.IsSuccess) throw new PairingRejectedException(response.StatusCode, DescribeRejection(response.StatusCode));

        Tlv8.Map map = Tlv8.Decode(response.Body);
        PairingTlv.ThrowIfError(map);
        return map;
    }

    private static byte[] Require(Tlv8.Map map, Tlv8Type tag, string what) => PairingTlv.Require(map, tag, what);

    private static string DescribeRejection(int code) => code switch
    {
        403 => "The device refused pairing (403). On a Mac: System Settings → AirDrop & Handoff → AirPlay Receiver → “Allow AirPlay for: Everyone” and turn off “Require Password” (a Mac may still only accept Apple devices). On an Apple TV: Settings → AirPlay & HomeKit → Allow Access → “Anyone on the Same Network”.",
        470 => "The device needs PIN pairing but did not show a code. Set AirPlay access to “Anyone on the Same Network” on the device and try again.",
        _ => $"Pairing failed (HTTP {code})",
    };
}
