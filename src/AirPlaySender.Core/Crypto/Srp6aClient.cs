using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AirPlaySender.Core.Crypto;

/// <summary>
/// SRP-6a, 3072-bit group (RFC 5054), SHA-512, CLIENT side — HomeKit
/// pair-setup. The username is always "Pair-Setup"; the password is the
/// PIN ("3939" for transient HomePod/macOS pairing, the on-screen 4-digit
/// code for Apple TV).
///
/// Padding convention: <c>k</c> and <c>u</c> are hashed over N-length
/// zero-padded big-endian operands ("H_nn_pad" in HomeKit reference
/// implementations); <c>salt</c>/<c>A</c>/<c>B</c>/<c>N</c>/<c>g</c> are
/// hashed at their natural (unpadded) byte length elsewhere. Getting this
/// wrong silently produces a session key the receiver rejects — there is
/// no partial-credit failure mode with SRP.
///
/// Wire-format reference: this is a straight port (BigInteger instead of
/// bignum) of the recipe in akustikrausch/airplay2-sender-cpp
/// (SPDX Apache-2.0, airplay_crypto.cpp), itself clean-room-documented
/// against pyatv's hap_srp.py and ejurgensen/pair_ap's pair_homekit.c and
/// verified against a real Apple TV 4K + macOS AirPlay receiver.
/// </summary>
public sealed class Srp6aClient
{
    private const int NBytes = 384; // 3072 bits

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

    private static readonly BigInteger G = 5;

    private BigInteger _a, _A;
    private string _password = "";
    private byte[] _salt = [];
    private BigInteger _K; // session key material as BigInteger of SHA512(S)
    private byte[] _sessionKey = [];
    private byte[] _proofM1 = [];
    private byte[] _publicA = [];
    private bool _processed;

    /// <summary>Chooses the password (PIN), generates ephemeral secret <c>a</c> and public <c>A = g^a mod N</c>.</summary>
    public void Start(string password)
    {
        _password = password;
        byte[] aBytes = RandomNumberGenerator.GetBytes(32); // 256-bit ephemeral, like the reference recipe
        _a = ToUnsignedBigEndian(aBytes);
        _A = BigInteger.ModPow(G, _a, N);
        _publicA = FromBigEndian(_A);
    }

    /// <summary>Public client value A (big-endian, natural length) — the wire form HAP TLV8 PublicKey carries.</summary>
    public byte[] PublicA => _publicA;

    /// <summary>
    /// Processes the server's salt + public B. Computes the shared session
    /// key K and the client proof M1. Returns false if B ≡ 0 (mod N), a
    /// malicious/garbled server value.
    /// </summary>
    public bool Process(byte[] salt, byte[] serverB)
    {
        _salt = salt;
        BigInteger B = ToUnsignedBigEndian(serverB);

        if (B % N == 0) return false;

        BigInteger k = ToUnsignedBigEndian(Sha.Sha512(Concat(PadTo(FromBigEndian(N), NBytes), PadTo(FromBigEndian(G), NBytes))));
        BigInteger u = ToUnsignedBigEndian(Sha.Sha512(Concat(PadTo(_publicA, NBytes), PadTo(FromBigEndian(B), NBytes))));

        byte[] usernamePassword = Encoding.ASCII.GetBytes("Pair-Setup:" + _password);
        byte[] ucpHash = Sha.Sha512(usernamePassword);
        BigInteger x = ToUnsignedBigEndian(Sha.Sha512(Concat(salt, ucpHash)));

        BigInteger gx = BigInteger.ModPow(G, x, N);
        BigInteger kgx = (k * gx) % N;
        BigInteger baseVal = Mod(B - kgx, N);
        BigInteger exp = _a + u * x;
        BigInteger S = BigInteger.ModPow(baseVal, exp, N);

        _sessionKey = Sha.Sha512(FromBigEndian(S));
        _K = ToUnsignedBigEndian(_sessionKey);

        // M1 = H( H(N) XOR H(g) | H(I) | salt | A | B | K )  — N and g hashed at NATURAL length (g is a single 0x05 byte, NOT N-padded).
        byte[] hN = Sha.Sha512(FromBigEndian(N));
        byte[] hg = Sha.Sha512(FromBigEndian(G));
        byte[] hXor = new byte[64];
        for (int i = 0; i < 64; i++) hXor[i] = (byte)(hN[i] ^ hg[i]);
        byte[] hI = Sha.Sha512(Encoding.ASCII.GetBytes("Pair-Setup"));

        byte[] m1in = Concat(hXor, hI, salt, _publicA, FromBigEndian(B), _sessionKey);
        _proofM1 = Sha.Sha512(m1in);

        _processed = true;
        return true;
    }

    /// <summary>64-byte client proof M1.</summary>
    public byte[] ProofM1 => _proofM1;

    /// <summary>K = SHA-512(S), 64 bytes — the HKDF ikm for every derived key, and (truncated) the AirPlay-2 transient audio key.</summary>
    public byte[] SessionKey => _sessionKey;

    /// <summary>Verifies the server's proof M2 = H(A | M1 | K).</summary>
    public bool VerifyServerProof(byte[] serverM2)
    {
        if (!_processed) return false;
        byte[] m2 = Sha.Sha512(Concat(_publicA, _proofM1, _sessionKey));
        return CryptographicOperations.FixedTimeEquals(m2, serverM2);
    }

    // ── BigInteger <-> unsigned big-endian byte[] helpers ──────────────
    // System.Numerics.BigInteger's own (isUnsigned:true, isBigEndian:true)
    // overloads do exactly what mbedtls_mpi_write_binary/_read_binary do
    // in the reference recipe, so these are thin named wrappers for
    // readability at call sites, not reimplementations.

    private static BigInteger ToUnsignedBigEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] FromBigEndian(BigInteger v) => v.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static byte[] PadTo(byte[] naturalBigEndian, int length)
    {
        if (naturalBigEndian.Length >= length) return naturalBigEndian;
        var padded = new byte[length];
        naturalBigEndian.CopyTo(padded, length - naturalBigEndian.Length);
        return padded;
    }

    private static BigInteger Mod(BigInteger v, BigInteger m)
    {
        BigInteger r = v % m;
        return r < 0 ? r + m : r;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var outp = new byte[total];
        int off = 0;
        foreach (var p in parts) { p.CopyTo(outp, off); off += p.Length; }
        return outp;
    }
}
