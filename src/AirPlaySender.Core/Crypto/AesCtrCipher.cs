using System.Security.Cryptography;

namespace AirPlaySender.Core.Crypto;

/// <summary>
/// AES-128-CTR, standard 128-bit big-endian counter (confirmed against
/// UxPlay's <c>aes_ctr_init</c>, which is literally OpenSSL's
/// <c>EVP_aes_128_ctr()</c> — nothing bespoke). .NET has no built-in CTR
/// mode as of .NET 9 (checked: no <c>System.Security.Cryptography.AesCtr</c>
/// in the 9.0 BCL), so this builds it from ECB the standard way: encrypt
/// the counter block, XOR with plaintext, increment.
///
/// Used only for the legacy AirPlay-Mirroring pair-verify handshake (see
/// <see cref="Receiving.PairingAccessorySession"/>) — every other encrypted
/// channel in this project is ChaCha20-Poly1305 via <see cref="ChaCha20Poly1305Cipher"/>.
/// </summary>
public static class AesCtrCipher
{
    /// <summary>
    /// XORs <paramref name="data"/> with the AES-CTR keystream starting
    /// <paramref name="blockOffset"/> 16-byte blocks into the stream from
    /// <paramref name="iv16"/> — encryption and decryption are the same
    /// operation. The block-offset skip matches the reference's "fake
    /// round" (a whole unrelated encrypt-of-zeros call before decrypting
    /// the peer's signature) needed because both directions share one
    /// logical keystream derived from the same ECDH secret.
    /// </summary>
    public static byte[] Transform(byte[] key16, byte[] iv16, int blockOffset, ReadOnlySpan<byte> data)
    {
        using Aes aes = Aes.Create();
        aes.Key = key16;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform encryptor = aes.CreateEncryptor();

        byte[] counter = (byte[])iv16.Clone();
        for (int i = 0; i < blockOffset; i++) Increment(counter);

        byte[] output = new byte[data.Length];
        var keystream = new byte[16];
        int pos = 0;
        while (pos < data.Length)
        {
            encryptor.TransformBlock(counter, 0, 16, keystream, 0);
            int chunk = Math.Min(16, data.Length - pos);
            for (int i = 0; i < chunk; i++)
                output[pos + i] = (byte)(data[pos + i] ^ keystream[i]);
            pos += chunk;
            Increment(counter);
        }
        return output;
    }

    private static void Increment(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
            if (++counter[i] != 0) break; // no carry into the next byte
    }
}
