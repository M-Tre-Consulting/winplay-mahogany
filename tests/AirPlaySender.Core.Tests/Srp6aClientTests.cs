using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AirPlaySender.Core.Crypto;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// Cross-checks <see cref="Srp6aClient"/> against an SRP-6a SERVER implemented
/// independently, straight from the RFC 2945/5054 formulas, right here in the
/// test (it does not call into Srp6aClient's internals). This is the strongest
/// test available without real Apple hardware: it exercises the exact same
/// padding/hashing conventions the client uses and proves a fresh,
/// independently-written peer agrees on the session key and both proofs.
/// </summary>
public class Srp6aClientTests
{
    // Same RFC 5054 3072-bit group the client uses — duplicated here
    // deliberately (not referenced from Srp6aClient) so a typo in one
    // doesn't silently cancel out against the same typo in the other.
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

    [Theory]
    [InlineData("3939")]      // AirPlay 2 transient (HomePod/macOS) fixed PIN
    [InlineData("1234")]      // a plausible on-screen Apple TV PIN
    public void ClientInteroperatesWithIndependentServerImplementation(string pin)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);

        // ── independent "server": compute the verifier, then B ─────────
        BigInteger x = ComputeX(salt, pin);
        BigInteger v = BigInteger.ModPow(G, x, N);
        byte[] bBytes = RandomNumberGenerator.GetBytes(32);
        BigInteger b = Unsigned(bBytes);
        BigInteger k = Unsigned(Sha.Sha512(Concat(PadTo(N.ToByteArray(true, true), NBytes), PadTo(G.ToByteArray(true, true), NBytes))));
        BigInteger B = (k * v + BigInteger.ModPow(G, b, N)) % N;
        byte[] serverBBytes = B.ToByteArray(true, true);

        // ── client (the code under test) ────────────────────────────────
        var client = new Srp6aClient();
        client.Start(pin);
        bool ok = client.Process(salt, serverBBytes);
        Assert.True(ok);

        // ── server derives S independently: S = (A * v^u) ^ b mod N ─────
        BigInteger A = Unsigned(client.PublicA);
        BigInteger u = Unsigned(Sha.Sha512(Concat(PadTo(client.PublicA, NBytes), PadTo(serverBBytes, NBytes))));
        BigInteger Sserver = BigInteger.ModPow(A * BigInteger.ModPow(v, u, N) % N, b, N);
        byte[] serverK = Sha.Sha512(Sserver.ToByteArray(true, true));

        Assert.Equal(serverK, client.SessionKey);

        // ── server verifies client's M1, then computes M2; client must accept it ──
        byte[] hN = Sha.Sha512(N.ToByteArray(true, true));
        byte[] hg = Sha.Sha512(G.ToByteArray(true, true));
        var hXor = new byte[64];
        for (int i = 0; i < 64; i++) hXor[i] = (byte)(hN[i] ^ hg[i]);
        byte[] hI = Sha.Sha512(Encoding.ASCII.GetBytes("Pair-Setup"));
        byte[] expectedM1 = Sha.Sha512(Concat(hXor, hI, salt, client.PublicA, serverBBytes, serverK));
        Assert.Equal(expectedM1, client.ProofM1);

        byte[] serverM2 = Sha.Sha512(Concat(client.PublicA, client.ProofM1, serverK));
        Assert.True(client.VerifyServerProof(serverM2));

        // A forged M2 must be rejected.
        byte[] forgedM2 = (byte[])serverM2.Clone();
        forgedM2[0] ^= 0xFF;
        Assert.False(client.VerifyServerProof(forgedM2));
    }

    [Fact]
    public void RejectsServerPublicKeyThatIsZeroModN()
    {
        var client = new Srp6aClient();
        client.Start("3939");
        Assert.False(client.Process(RandomNumberGenerator.GetBytes(16), N.ToByteArray(true, true))); // B == N ≡ 0 (mod N)
    }

    private static BigInteger ComputeX(byte[] salt, string password)
    {
        byte[] ucpHash = Sha.Sha512(Encoding.ASCII.GetBytes("Pair-Setup:" + password));
        return Unsigned(Sha.Sha512(Concat(salt, ucpHash)));
    }

    private static BigInteger Unsigned(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] PadTo(byte[] naturalBigEndian, int length)
    {
        if (naturalBigEndian.Length >= length) return naturalBigEndian;
        var padded = new byte[length];
        naturalBigEndian.CopyTo(padded, length - naturalBigEndian.Length);
        return padded;
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
