using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Tlv;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// HAP pair-setup, accessory (SRP-6a server) role — the TRANSIENT shape
/// only (no PIN, no long-term identity exchange, fixed password "3939",
/// stops at M4): the same shape this project's own Phase 1 controller code
/// already speaks against a HomePod-class receiver
/// (<see cref="Pairing.PairSetupClient.RunTransientAsync"/>), and the exact
/// SRP-6a-3072 math this project has ALREADY proven correct end-to-end —
/// this is that same server-role math (originally written for
/// <c>tests/.../FakeAirPlay2Receiver.cs</c>, a test double this project's
/// own test suite has exercised for a while), copied into production code
/// rather than reinvented, to keep the one genuinely new-risk piece (the
/// group math) at zero instead of adding a second, independently-risky
/// implementation of the same thing.
///
/// <b>This is a documented guess, not a verified fact — read the doc
/// comment on <see cref="HapPairVerifyAccessorySession"/> for why.</b> The
/// one real capture available (a real Hisense TV, mirroring — see README)
/// skipped pair-setup entirely (the phone was already paired from before),
/// so there is zero real-traffic evidence for whether mirroring's pair-setup
/// is actually this transient shape, the PIN/identity shape Phase 1 also
/// has full reference code for (<see cref="Pairing.PairSetupClient.RunPinAsync"/>),
/// or something else again under the same unfamiliar <c>X-Apple-HKP: 6</c>
/// this project has only ever seen on mirroring's pair-verify. Transient is
/// the reasoned starting guess — this project's own mDNS TXT record already
/// advertises <c>pw=false</c> (no PIN), and transient is the "no user
/// interaction" shape that matches — but it is still a guess, and the
/// right way to resolve it is another real capture of an iPhone pairing
/// with this TV *for the first time*, not more reasoning from a desk.
/// </summary>
public sealed class HapPairSetupAccessorySession
{
    private const string TransientPassword = "3939";

    private static readonly BigInteger N = BigInteger.Parse("00" +
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183" +
        "995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A" +
        "85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7A" +
        "BF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D" +
        "87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E20" +
        "8E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF",
        System.Globalization.NumberStyles.HexNumber);
    private const int NBytes = 384;
    private static readonly BigInteger G = 5;

    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly BigInteger _b = new(RandomNumberGenerator.GetBytes(32), isUnsigned: true, isBigEndian: true);
    private BigInteger _serverB, _verifier;

    public bool IsComplete { get; private set; }

    /// <summary>The raw 64-byte SRP session key, available once <see cref="IsComplete"/> is true — feed to <c>SessionKeyDerivation</c>-shaped HKDF calls the same way Phase 1's transient pair-setup does.</summary>
    public byte[]? SessionKey { get; private set; }

    /// <summary>Returns the TLV8 response body, or null to signal "reject — close the connection" (malformed request, wrong method/flags, or a proof that doesn't verify).</summary>
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
        // Only the transient shape is implemented — see the class doc comment.
        // A PIN-flow M1 (no Flags TLV, or Flags without bit 0x10) isn't
        // something this class can serve; refuse rather than silently
        // mishandle it.
        byte[]? flags = req.Get(Tlv8Type.Flags);
        if (flags is not { Length: >= 1 } || (flags[0] & 0x10) == 0) return null;

        BigInteger x = ComputeX(_salt, TransientPassword);
        BigInteger v = BigInteger.ModPow(G, x, N);
        BigInteger bPublic = (HashPaddedPair(N, G) * v + BigInteger.ModPow(G, _b, N)) % N;
        _serverB = bPublic;
        _verifier = v;

        var m2 = new Tlv8.Map();
        m2.Add(Tlv8Type.State, 0x02);
        m2.Add(Tlv8Type.Salt, _salt);
        m2.Add(Tlv8Type.PublicKey, bPublic.ToByteArray(isUnsigned: true, isBigEndian: true));
        return Tlv8.Encode(m2);
    }

    private byte[]? HandleM3(Tlv8.Map req)
    {
        byte[]? aBytes = req.Get(Tlv8Type.PublicKey);
        byte[]? clientProof = req.Get(Tlv8Type.Proof);
        if (aBytes is null || clientProof is null) return null;
        BigInteger clientA = new(aBytes, isUnsigned: true, isBigEndian: true);

        BigInteger u = HashPaddedPair(clientA, _serverB);
        BigInteger s = BigInteger.ModPow(clientA * BigInteger.ModPow(_verifier, u, N) % N, _b, N);
        byte[] sessionKey = Sha.Sha512(s.ToByteArray(isUnsigned: true, isBigEndian: true));

        byte[] hN = Sha.Sha512(N.ToByteArray(isUnsigned: true, isBigEndian: true));
        byte[] hg = Sha.Sha512(G.ToByteArray(isUnsigned: true, isBigEndian: true));
        var hXor = new byte[64];
        for (int i = 0; i < 64; i++) hXor[i] = (byte)(hN[i] ^ hg[i]);
        byte[] hI = Sha.Sha512("Pair-Setup"u8.ToArray());
        byte[] expectedM1 = Sha.Sha512(Concat(hXor, hI, _salt, aBytes, _serverB.ToByteArray(isUnsigned: true, isBigEndian: true), sessionKey));

        if (!CryptographicOperations.FixedTimeEquals(expectedM1, clientProof)) return null;

        byte[] serverM2 = Sha.Sha512(Concat(aBytes, clientProof, sessionKey));
        IsComplete = true;
        SessionKey = sessionKey;

        var m4 = new Tlv8.Map();
        m4.Add(Tlv8Type.State, 0x04);
        m4.Add(Tlv8Type.Proof, serverM2);
        return Tlv8.Encode(m4);
    }

    private static BigInteger ComputeX(byte[] salt, string password)
    {
        byte[] ucpHash = Sha.Sha512(Encoding.ASCII.GetBytes("Pair-Setup:" + password));
        return new BigInteger(Sha.Sha512(Concat(salt, ucpHash)), isUnsigned: true, isBigEndian: true);
    }

    private static BigInteger HashPaddedPair(BigInteger a, BigInteger b) =>
        new(Sha.Sha512(Concat(PadTo(a, NBytes), PadTo(b, NBytes))), isUnsigned: true, isBigEndian: true);

    private static byte[] PadTo(BigInteger v, int length)
    {
        byte[] natural = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (natural.Length >= length) return natural;
        var padded = new byte[length];
        natural.CopyTo(padded, length - natural.Length);
        return padded;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
