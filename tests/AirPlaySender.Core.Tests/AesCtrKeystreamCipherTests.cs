using AirPlaySender.Core.Crypto;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// Found by code review, never exercised against real hardware (the
/// mirroring data channel has never actually received a packet in any test
/// so far): <see cref="AirPlaySender.Core.Receiving.MirroringDataReceiver"/> used to decrypt every
/// packet independently at keystream block 0, instead of continuing the
/// AES-CTR keystream across the connection the way a real H.264 byte stream
/// (arbitrary, essentially never 16-byte-aligned packet lengths) needs —
/// matching UxPlay's <c>mirror_buffer_decrypt</c>. These pin the property
/// that actually matters: splitting one logical stream into differently-
/// sized chunks must decrypt identically to processing it in one call,
/// no matter where the split lands relative to a 16-byte block boundary.
/// </summary>
public class AesCtrKeystreamCipherTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Iv = Enumerable.Range(0, 16).Select(i => (byte)(0xF0 + i)).ToArray();

    [Fact]
    public void SplittingAtAnArbitraryNonBlockAlignedOffsetMatchesOneShot()
    {
        byte[] plaintext = Enumerable.Range(0, 200).Select(i => (byte)(i * 7 % 251)).ToArray();

        byte[] oneShot = new AesCtrKeystreamCipher(Key, Iv).Transform(plaintext);

        // 37 and 61 are both deliberately not multiples of 16, and 37+61 < 200,
        // so this exercises a mid-block split, a run of full blocks, and a
        // second mid-block split all in the same test.
        var streamed = new AesCtrKeystreamCipher(Key, Iv);
        byte[] part1 = streamed.Transform(plaintext.AsSpan(0, 37));
        byte[] part2 = streamed.Transform(plaintext.AsSpan(37, 61));
        byte[] part3 = streamed.Transform(plaintext.AsSpan(98, plaintext.Length - 98));
        byte[] rejoined = [.. part1, .. part2, .. part3];

        Assert.Equal(oneShot, rejoined);
    }

    [Fact]
    public void SplittingOneByteAtATimeStillMatchesOneShot()
    {
        byte[] plaintext = Enumerable.Range(0, 50).Select(i => (byte)i).ToArray();
        byte[] oneShot = new AesCtrKeystreamCipher(Key, Iv).Transform(plaintext);

        var streamed = new AesCtrKeystreamCipher(Key, Iv);
        var rejoined = new List<byte>();
        foreach (byte b in plaintext)
            rejoined.AddRange(streamed.Transform([b]));

        Assert.Equal(oneShot, rejoined);
    }

    [Fact]
    public void DecryptingItsOwnCiphertextRecoversThePlaintext()
    {
        byte[] plaintext = Enumerable.Range(0, 100).Select(i => (byte)(i * 3)).ToArray();

        byte[] ciphertext = new AesCtrKeystreamCipher(Key, Iv).Transform(plaintext);
        byte[] roundTripped = new AesCtrKeystreamCipher(Key, Iv).Transform(ciphertext); // CTR: same operation both ways

        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void FirstCallMatchesTheExistingStatelessCipherAtBlockOffsetZero()
    {
        // No regression on the one path that WAS already exercised (packet #1 of a session).
        byte[] plaintext = Enumerable.Range(0, 48).Select(i => (byte)(200 - i)).ToArray();

        byte[] stateless = AesCtrCipher.Transform(Key, Iv, blockOffset: 0, plaintext);
        byte[] stateful = new AesCtrKeystreamCipher(Key, Iv).Transform(plaintext);

        Assert.Equal(stateless, stateful);
    }
}
