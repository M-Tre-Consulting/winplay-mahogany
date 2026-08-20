using NSec.Cryptography;

namespace AirPlaySender.Core.Crypto;

/// <summary>
/// Ed25519 sign/verify for HAP's long-term identity (pyatv "ltsk"/"ltpk").
/// <paramref name="seed32"/> throughout is the 32-byte secret seed; the
/// public key is deterministically derived from it.
/// </summary>
public static class Ed25519Signer
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    public static byte[] GenerateSeed()
    {
        using Key key = Key.Create(Algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return key.Export(KeyBlobFormat.RawPrivateKey);
    }

    public static byte[] PublicFromSeed(ReadOnlySpan<byte> seed32)
    {
        using Key key = Import(seed32);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed32, ReadOnlySpan<byte> message)
    {
        using Key key = Import(seed32);
        return Algorithm.Sign(key, message);
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature64)
    {
        if (publicKey32.Length != 32 || signature64.Length != 64) return false;
        NSec.Cryptography.PublicKey pub = NSec.Cryptography.PublicKey.Import(Algorithm, publicKey32, KeyBlobFormat.RawPublicKey);
        return Algorithm.Verify(pub, message, signature64);
    }

    private static Key Import(ReadOnlySpan<byte> seed32) =>
        Key.Import(Algorithm, seed32, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
}
