using NSec.Cryptography;

namespace AirPlaySender.Core.Crypto;

/// <summary>
/// X25519 ECDH for HAP pair-verify. Wraps an NSec <see cref="Key"/> so
/// clamping/scalar handling is libsodium's, not hand-rolled.
/// </summary>
public sealed class X25519KeyPair : IDisposable
{
    private readonly Key _key;

    /// <summary>Our 32-byte public value (the wire form the TLV8 PublicKey tag carries).</summary>
    public byte[] PublicKey { get; }

    private X25519KeyPair(Key key)
    {
        _key = key;
        PublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public static X25519KeyPair Generate()
    {
        var key = Key.Create(KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None });
        return new X25519KeyPair(key);
    }

    /// <summary>
    /// Computes the shared secret with <paramref name="theirPublicKey32"/>.
    /// Returns null for a malformed/low-order peer key (NSec rejects those
    /// internally), mirroring the reference recipe's "reject an all-zero
    /// shared secret" defense — a legitimate receiver never triggers this.
    /// </summary>
    public byte[]? Agree(ReadOnlySpan<byte> theirPublicKey32)
    {
        if (theirPublicKey32.Length != 32) return null;
        NSec.Cryptography.PublicKey peer = NSec.Cryptography.PublicKey.Import(
            KeyAgreementAlgorithm.X25519, theirPublicKey32, KeyBlobFormat.RawPublicKey);
        SharedSecret? shared = KeyAgreementAlgorithm.X25519.Agree(_key, peer,
            new SharedSecretCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        if (shared is null) return null;
        using (shared)
            return shared.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    public void Dispose() => _key.Dispose();
}
