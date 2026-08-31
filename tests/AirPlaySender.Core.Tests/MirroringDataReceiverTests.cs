using System.Text;
using System.Threading;
using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Receiving;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// Regression test for the bug a live mirroring session hit: a real device's
/// <c>streamConnectionID</c> can be negative once decoded as a signed 64-bit
/// <see cref="long"/> (the plist's own encoding), but UxPlay's real
/// <c>mirror_buffer_init_aes</c> (lib/mirror_buffer.c) formats it with
/// <c>PRIu64</c> — unsigned — when building the hash input. Formatting the
/// signed value directly (i.e. with a leading "-") derives the wrong AES-CTR
/// key: decryption "succeeds" (no exception — AES-CTR always produces
/// *some* bytes) but every video NAL comes out as garbage, silently, while
/// the unrelated unencrypted SPS/PPS packet keeps decoding fine — exactly
/// what made this hard to spot live (see MirroringDataReceiver.cs's doc
/// comment on DeriveVideoKeyIv, and the README).
/// </summary>
public class MirroringDataReceiverTests
{
    [Fact]
    public void DerivesTheKeyFromTheUnsignedDecimalOfANegativeStreamConnectionId()
    {
        // The exact streamConnectionID a real iPhone 13 Pro Max sent in a live session.
        const long negativeStreamConnectionId = -292324589914665516;
        // Its bit pattern reinterpreted as unsigned (confirmed independently: 2^64 + id).
        const string unsignedDecimal = "18154419483794886100";

        byte[] sessionAesKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        (byte[] key, byte[] iv) = MirroringDataReceiver.DeriveVideoKeyIv(sessionAesKey, negativeStreamConnectionId);

        byte[] expectedKey = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamKey{unsignedDecimal}"), .. sessionAesKey])[..16];
        byte[] expectedIv = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamIV{unsignedDecimal}"), .. sessionAesKey])[..16];

        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedIv, iv);
    }

    [Fact]
    public void DoesNotDeriveTheKeyFromTheSignedDecimalOfANegativeStreamConnectionId()
    {
        // Pins down the regression directly: the naive (wrong) implementation
        // that formats the signed `long` as-is (with its leading "-") must NOT
        // be what this produces.
        const long negativeStreamConnectionId = -292324589914665516;
        byte[] sessionAesKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        (byte[] key, byte[] iv) = MirroringDataReceiver.DeriveVideoKeyIv(sessionAesKey, negativeStreamConnectionId);

        byte[] wrongKey = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamKey{negativeStreamConnectionId}"), .. sessionAesKey])[..16];
        byte[] wrongIv = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamIV{negativeStreamConnectionId}"), .. sessionAesKey])[..16];

        Assert.NotEqual(wrongKey, key);
        Assert.NotEqual(wrongIv, iv);
    }

    [Fact]
    public void AgreesWithTheSignedFormattingForAPositiveStreamConnectionId()
    {
        // For a positive id the signed and unsigned decimal strings are
        // identical, so this is also a sanity check that the fix didn't
        // change behavior for the (far more common, and the only case a
        // previous live session ever happened to exercise) positive case.
        const long positiveStreamConnectionId = 4791023875L;
        byte[] sessionAesKey = Enumerable.Range(0, 16).Select(i => (byte)(i * 7)).ToArray();

        (byte[] key, byte[] iv) = MirroringDataReceiver.DeriveVideoKeyIv(sessionAesKey, positiveStreamConnectionId);

        byte[] expectedKey = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamKey{positiveStreamConnectionId}"), .. sessionAesKey])[..16];
        byte[] expectedIv = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamIV{positiveStreamConnectionId}"), .. sessionAesKey])[..16];

        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedIv, iv);
    }

    [Fact]
    public async Task RaisesSessionEndedOnceWhenDisposed()
    {
        // Regression test for the other live bug found the same night:
        // nothing ever told a renderer (MirrorWindow) that the mirroring
        // session had ended, so its window just sat there showing a frozen
        // last frame after the phone stopped mirroring. SessionEnded is the
        // fix; DisposeAsync (via cancelling the listener's accept) is the
        // simplest reliable way to exercise AcceptLoopAsync's exit path
        // without needing a real TCP peer to connect and disconnect.
        var receiver = new MirroringDataReceiver();
        int sessionEndedCount = 0;
        receiver.SessionEnded += () => Interlocked.Increment(ref sessionEndedCount);

        receiver.Start();
        await receiver.DisposeAsync();

        Assert.Equal(1, sessionEndedCount);
    }
}
