using System.Security.Cryptography;

namespace AirPlaySender.Core.Crypto;

/// <summary>
/// Stateful AES-128-CTR that continues the keystream across multiple
/// <see cref="Transform"/> calls — unlike <see cref="AesCtrCipher"/> (a
/// one-shot transform for the pair-verify handshake, where each call really
/// is independent), a mirroring video TCP connection is one continuous
/// CTR-encrypted byte stream split across many packets whose payload
/// lengths are arbitrary (real H.264 NAL sizes, essentially never a
/// multiple of 16 bytes) — so the keystream position at the end of one
/// packet has to carry into the next.
///
/// Algorithm matches UxPlay's <c>mirror_buffer_decrypt</c>
/// (<c>lib/mirror_buffer.c</c>) byte-for-byte in effect, restated with an
/// explicit leftover-keystream buffer instead of its decrypt-of-zeros
/// trick: when a packet ends mid-block, the unused tail of that block's
/// keystream is kept and XORed against the next packet's opening bytes
/// before any new AES calls happen for that packet.
/// </summary>
public sealed class AesCtrKeystreamCipher
{
    private readonly ICryptoTransform _encryptor;
    private readonly Aes _aes;
    private readonly byte[] _counter;
    private readonly byte[] _leftoverKeystream = new byte[16];
    private int _leftoverCount; // how many trailing bytes of _leftoverKeystream are still unused

    public AesCtrKeystreamCipher(byte[] key16, byte[] iv16)
    {
        _aes = Aes.Create();
        _aes.Key = key16;
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _encryptor = _aes.CreateEncryptor();
        _counter = (byte[])iv16.Clone();
    }

    /// <summary>XORs <paramref name="data"/> with the next slice of the running keystream. Same operation for encrypt or decrypt.</summary>
    public byte[] Transform(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        int pos = 0;

        if (_leftoverCount > 0)
        {
            int n = Math.Min(_leftoverCount, data.Length);
            int leftoverStart = 16 - _leftoverCount;
            for (int i = 0; i < n; i++)
                output[i] = (byte)(data[i] ^ _leftoverKeystream[leftoverStart + i]);
            pos = n;
            _leftoverCount -= n;
        }

        var keystream = new byte[16];
        while (data.Length - pos >= 16)
        {
            NextKeystreamBlock(keystream);
            for (int i = 0; i < 16; i++)
                output[pos + i] = (byte)(data[pos + i] ^ keystream[i]);
            pos += 16;
        }

        int rest = data.Length - pos;
        if (rest > 0)
        {
            NextKeystreamBlock(keystream);
            for (int i = 0; i < rest; i++)
                output[pos + i] = (byte)(data[pos + i] ^ keystream[i]);
            Array.Copy(keystream, _leftoverKeystream, 16);
            _leftoverCount = 16 - rest;
        }

        return output;
    }

    private void NextKeystreamBlock(byte[] keystream)
    {
        _encryptor.TransformBlock(_counter, 0, 16, keystream, 0);
        Increment(_counter);
    }

    private static void Increment(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
            if (++counter[i] != 0) break; // no carry into the next byte
    }
}
