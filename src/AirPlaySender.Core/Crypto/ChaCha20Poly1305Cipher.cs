using NSec.Cryptography;

namespace AirPlaySender.Core.Crypto;

/// <summary>
/// ChaCha20-Poly1305 AEAD exactly as HAP/AirPlay 2 uses it: an 8-byte
/// little-endian counter nonce, left-padded with 4 zero bytes to the
/// 12-byte IETF nonce size. Every encrypted channel in AirPlay 2 (the RTSP
/// control channel, the event channel, and the realtime audio payload)
/// uses this exact framing, just with different keys/counters.
///
/// Backed by NSec (libsodium), not the BCL's <c>ChaCha20Poly1305</c> class:
/// the BCL type throws <see cref="System.PlatformNotSupportedException"/>
/// unless the OS's CNG provider implements it, which is not guaranteed on
/// every Windows 10/11 build. NSec ships its own native implementation, so
/// it works everywhere regardless of OS crypto-provider support.
/// </summary>
public static class ChaCha20Poly1305Cipher
{
    private static readonly AeadAlgorithm Algorithm = AeadAlgorithm.ChaCha20Poly1305;

    /// <summary>Builds the 8-byte little-endian counter nonce HAP uses for
    /// the audio + control/event channels (the 4-byte zero pad is added
    /// internally by <see cref="Encrypt"/>/<see cref="TryDecrypt"/>).</summary>
    public static byte[] CounterNonce8(ulong counter)
    {
        var n = new byte[8];
        for (int i = 0; i < 8; i++) n[i] = (byte)((counter >> (8 * i)) & 0xFF);
        return n;
    }

    private static byte[] Pad12(ReadOnlySpan<byte> nonce8)
    {
        var n = new byte[12];
        nonce8[..Math.Min(8, nonce8.Length)].CopyTo(n.AsSpan(4));
        return n;
    }

    /// <summary>Returns ciphertext||tag(16).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> key32, ReadOnlySpan<byte> nonce8,
        ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad = default)
    {
        using Key key = ImportKey(key32);
        Span<byte> nonce = Pad12(nonce8);
        return Algorithm.Encrypt(key, nonce, aad, plaintext);
    }

    /// <summary>Returns null if the authentication tag is invalid.</summary>
    public static byte[]? TryDecrypt(ReadOnlySpan<byte> key32, ReadOnlySpan<byte> nonce8,
        ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad = default)
    {
        if (ciphertextAndTag.Length < 16) return null;
        using Key key = ImportKey(key32);
        Span<byte> nonce = Pad12(nonce8);
        var plaintext = new byte[ciphertextAndTag.Length - Algorithm.TagSize];
        return Algorithm.Decrypt(key, nonce, aad, ciphertextAndTag, plaintext) ? plaintext : null;
    }

    private static Key ImportKey(ReadOnlySpan<byte> key32)
    {
        if (key32.Length != 32) throw new ArgumentException("ChaCha20-Poly1305 key must be 32 bytes", nameof(key32));
        return Key.Import(Algorithm, key32, KeyBlobFormat.RawSymmetricKey);
    }
}
