using System.Security.Cryptography;

namespace AirPlaySender.Core.Crypto;

/// <summary>
/// Thin SHA-512 helpers. HomeKit/AirPlay pairing (SRP-6a-3072, HKDF) is
/// entirely SHA-512 based.
/// </summary>
public static class Sha
{
    public static byte[] Sha512(ReadOnlySpan<byte> data) => System.Security.Cryptography.SHA512.HashData(data);

    public static byte[] HmacSha512(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) =>
        HMACSHA512.HashData(key, data);
}
