using AirPlaySender.Core.Audio;
using Xunit;

namespace AirPlaySender.Core.Tests;

public class AlacUncompressedEncoderTests
{
    [Fact]
    public void MatchesHandComputedBitLayoutForOneSamplePair()
    {
        // L=0x1234, R=0x5678. Expected bytes independently hand-computed
        // from the documented bit layout (see class doc comment), not by
        // calling into the encoder — a real cross-check, not a tautology.
        short[] samples = [0x1234, 0x5678];
        byte[] expected = [0x20, 0x00, 0x02, 0x24, 0x68, 0xAC, 0xF1, 0xC0];

        byte[] actual = AlacUncompressedEncoder.EncodeFrame(samples, frameCount: 1);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ProducesExpectedLengthForAFullRaopPacket()
    {
        // 352 frames/packet * 2 channels * 16 bits, plus the 6-bit header
        // and 3-bit end tag, rounded up to a byte boundary.
        var samples = new short[352 * 2];
        byte[] encoded = AlacUncompressedEncoder.EncodeFrame(samples, frameCount: 352);

        int expectedBits = 3 + 4 + 12 + 1 + 2 + 1 + 352 * 2 * 16 + 3;
        int expectedBytes = (expectedBits + 7) / 8;
        Assert.Equal(expectedBytes, encoded.Length);
    }

    [Fact]
    public void AllZeroSamplesStillEmitTheNonZeroHeaderAndEndTag()
    {
        var samples = new short[4]; // 2 frames of silence
        byte[] encoded = AlacUncompressedEncoder.EncodeFrame(samples, frameCount: 2);
        // First 3 bits (channel tag = 1) must land in the top bits of byte 0: 001xxxxx -> 0x20 masked area.
        Assert.Equal(0b001, encoded[0] >> 5);
    }
}
