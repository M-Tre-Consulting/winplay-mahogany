namespace AirPlaySender.Core.Crypto;

/// <summary>
/// The HomeKit/AirPlay 2 encrypted-stream framing used by BOTH the RTSP
/// control channel and the event channel: each frame on the wire is
/// <c>[2-byte little-endian length][ChaCha20-Poly1305 ciphertext][16-byte tag]</c>,
/// AAD = the 2 length bytes, nonce = an 8-byte little-endian per-direction
/// frame counter. Shared here so <c>RtspConnection</c> and
/// <c>AirPlayEventChannel</c> (encrypt outgoing on one counter, decrypt
/// incoming on an independent counter, each keyed differently) can't drift
/// apart on this framing.
/// </summary>
public static class HapFrameCodec
{
    /// <summary>Encrypts <paramref name="plaintext"/> as one on-the-wire frame (length prefix included). Advances <paramref name="counter"/>.</summary>
    public static byte[] EncryptFrame(byte[] key, ref ulong counter, ReadOnlySpan<byte> plaintext)
    {
        byte[] lenPrefix = [(byte)(plaintext.Length & 0xFF), (byte)((plaintext.Length >> 8) & 0xFF)];
        byte[] nonce = ChaCha20Poly1305Cipher.CounterNonce8(counter++);
        byte[] ciphertext = ChaCha20Poly1305Cipher.Encrypt(key, nonce, plaintext, lenPrefix);
        var frame = new byte[2 + ciphertext.Length];
        lenPrefix.CopyTo(frame, 0);
        ciphertext.CopyTo(frame, 2);
        return frame;
    }

    /// <summary>
    /// Encrypts a (possibly large) plaintext as a sequence of 1024-byte frames — the
    /// chunking the reference recipe uses for the RTSP control channel.
    /// </summary>
    public static byte[] EncryptChunked(byte[] key, ref ulong counter, ReadOnlySpan<byte> plaintext, int chunkSize = 1024)
    {
        using var outBuf = new MemoryStream();
        int off = 0;
        while (off < plaintext.Length)
        {
            int len = Math.Min(chunkSize, plaintext.Length - off);
            byte[] frame = EncryptFrame(key, ref counter, plaintext.Slice(off, len));
            outBuf.Write(frame);
            off += len;
        }
        return outBuf.ToArray();
    }

    /// <summary>
    /// If <paramref name="buffer"/> holds at least one complete frame at its
    /// front, decrypts and removes it, returning the plaintext. Returns null
    /// (buffer untouched) if more bytes are needed. Throws if the tag is
    /// invalid — a forged/corrupted frame means the session can no longer be trusted.
    /// </summary>
    public static byte[]? TryDecryptNextFrame(byte[] key, ref ulong counter, List<byte> buffer)
    {
        if (buffer.Count < 2) return null;
        int len = buffer[0] | (buffer[1] << 8);
        int need = 2 + len + 16;
        if (buffer.Count < need) return null;

        byte[] lenPrefix = [buffer[0], buffer[1]];
        byte[] ciphertextAndTag = buffer.GetRange(2, len + 16).ToArray();
        byte[] nonce = ChaCha20Poly1305Cipher.CounterNonce8(counter++);
        byte[]? plaintext = ChaCha20Poly1305Cipher.TryDecrypt(key, nonce, ciphertextAndTag, lenPrefix);
        if (plaintext is null)
            throw new IOException("AirPlay 2 encrypted channel failed authentication — the session can no longer be trusted");

        buffer.RemoveRange(0, need);
        return plaintext;
    }
}
