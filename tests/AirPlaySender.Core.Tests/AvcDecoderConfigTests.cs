using AirPlaySender.Core.Receiving;
using Xunit;

namespace AirPlaySender.Core.Tests;

public class AvcDecoderConfigTests
{
    [Fact]
    public void ParsesASingleSpsAndPpsRecord()
    {
        // version=1, profile=0x64, compat=0x00, level=0x1E, lengthSize byte=0xFF,
        // numSPS byte=0xE1 (1 SPS), SPS len=3 {0x67,0xAA,0xBB}, numPPS=1, PPS len=2 {0x68,0xCC}.
        byte[] record = Convert.FromHexString("0164001EFFE1000367AABB01000268CC");

        (byte[] Sps, byte[] Pps)? result = AvcDecoderConfig.TryParse(record);

        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x67, 0xAA, 0xBB }, result!.Value.Sps);
        Assert.Equal(new byte[] { 0x68, 0xCC }, result.Value.Pps);
    }

    [Fact]
    public void RejectsAWrongVersionByte()
    {
        byte[] record = Convert.FromHexString("0264001EFFE1000367AABB01000268CC");
        Assert.Null(AvcDecoderConfig.TryParse(record));
    }

    [Fact]
    public void BuildRecordIsTheExactInverseOfTryParse()
    {
        byte[] sps = [0x67, 0x64, 0x00, 0x1E, 0xAA, 0xBB];
        byte[] pps = [0x68, 0xCC, 0xDD];

        byte[] record = AvcDecoderConfig.BuildRecord(sps, pps);
        (byte[] Sps, byte[] Pps)? roundTripped = AvcDecoderConfig.TryParse(record);

        Assert.NotNull(roundTripped);
        Assert.Equal(sps, roundTripped!.Value.Sps);
        Assert.Equal(pps, roundTripped.Value.Pps);
    }

    [Fact]
    public void SplitsOneAvccNalUnit()
    {
        byte[] data = [0x00, 0x00, 0x00, 0x03, 0xAA, 0xBB, 0xCC];
        List<byte[]> nals = AvcDecoderConfig.SplitAvccNalUnits(data);
        Assert.Single(nals);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, nals[0]);
    }

    [Fact]
    public void SplitsTwoConsecutiveAvccNalUnits()
    {
        byte[] data = [0x00, 0x00, 0x00, 0x02, 0x11, 0x22, 0x00, 0x00, 0x00, 0x01, 0x33];
        List<byte[]> nals = AvcDecoderConfig.SplitAvccNalUnits(data);
        Assert.Equal(2, nals.Count);
        Assert.Equal(new byte[] { 0x11, 0x22 }, nals[0]);
        Assert.Equal(new byte[] { 0x33 }, nals[1]);
    }

    [Fact]
    public void RewriteAvccToAnnexBInPlaceSwapsLengthPrefixesForStartCodes()
    {
        // Two NALs: [len 3][0x67 SPS ..] [len 2][0x65 IDR ..]
        byte[] data = [0x00, 0x00, 0x00, 0x03, 0x67, 0xAA, 0xBB, 0x00, 0x00, 0x00, 0x02, 0x65, 0xCC];

        bool ok = AvcDecoderConfig.RewriteAvccToAnnexBInPlace(data, out bool hasKeyFrame, out bool hasVcl, out bool startsWithSps);

        Assert.True(ok);
        Assert.True(hasKeyFrame);   // NAL type 5 present
        Assert.True(hasVcl);
        Assert.True(startsWithSps); // first NAL is type 7
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0xAA, 0xBB, 0x00, 0x00, 0x00, 0x01, 0x65, 0xCC }, data);
    }

    [Fact]
    public void RewriteAvccToAnnexBInPlaceReportsAPFrameOnlyPayload()
    {
        byte[] data = [0x00, 0x00, 0x00, 0x02, 0x41, 0x99]; // NAL type 1 = non-IDR slice
        bool ok = AvcDecoderConfig.RewriteAvccToAnnexBInPlace(data, out bool hasKeyFrame, out bool hasVcl, out bool startsWithSps);

        Assert.True(ok);
        Assert.False(hasKeyFrame);
        Assert.True(hasVcl);
        Assert.False(startsWithSps);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41, 0x99 }, data);
    }

    [Fact]
    public void RewriteAvccToAnnexBInPlaceReturnsFalseOnGarbage()
    {
        byte[] data = [0x7F, 0xFF, 0xFF, 0xFF, 0x01]; // absurd length prefix
        Assert.False(AvcDecoderConfig.RewriteAvccToAnnexBInPlace(data, out _, out _, out _));
    }
}
