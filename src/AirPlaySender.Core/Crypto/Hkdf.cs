using System.Security.Cryptography;
using System.Text;

namespace AirPlaySender.Core.Crypto;

/// <summary>
/// RFC 5869 HKDF-SHA512, ASCII salt/info (every HomeKit/AirPlay session key
/// derivation uses a fixed ASCII salt+info label pair, e.g.
/// ("Control-Salt", "Control-Write-Encryption-Key")). HAP always derives
/// 32-byte keys. Backed by the BCL's <see cref="System.Security.Cryptography.HKDF"/>,
/// which is a pure managed RFC 5869 implementation (no OS crypto-provider
/// dependency), so this works identically on every supported Windows version.
/// </summary>
public static class Hkdf
{
    public static byte[] DeriveSha512(string salt, string info, ReadOnlySpan<byte> ikm, int length = 32)
    {
        byte[] saltBytes = Encoding.ASCII.GetBytes(salt);
        byte[] infoBytes = Encoding.ASCII.GetBytes(info);
        return HKDF.DeriveKey(HashAlgorithmName.SHA512, ikm.ToArray(), length, saltBytes, infoBytes);
    }
}
