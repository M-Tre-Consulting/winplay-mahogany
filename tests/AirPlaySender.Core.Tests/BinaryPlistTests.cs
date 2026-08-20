using AirPlaySender.Core.Plist;
using Xunit;

namespace AirPlaySender.Core.Tests;

public class BinaryPlistTests
{
    [Fact]
    public void RoundTripsTheSetupSessionShapeOfDictionary()
    {
        PlistValue root = new PlistDictBuilder()
            .Add("deviceID", "AA:BB:CC:DD:EE:FF")
            .Add("sessionUUID", "1234-ABCD")
            .Add("timingPort", 12345L)
            .Add("isMultiSelectAirPlay", true)
            .Add("groupContainsGroupLeader", false)
            .Build();

        byte[] wire = BinaryPlist.Encode(root);
        Assert.StartsWith("bplist00", System.Text.Encoding.ASCII.GetString(wire, 0, 8));

        PlistValue? decoded = BinaryPlist.Decode(wire);
        Assert.NotNull(decoded);
        Assert.Equal("AA:BB:CC:DD:EE:FF", decoded!.Find("deviceID")!.AsStr());
        Assert.Equal("1234-ABCD", decoded.Find("sessionUUID")!.AsStr());
        Assert.Equal(12345L, decoded.Find("timingPort")!.AsInt());
        Assert.True(decoded.Find("isMultiSelectAirPlay")!.BoolValue);
        Assert.False(decoded.Find("groupContainsGroupLeader")!.BoolValue);
    }

    [Fact]
    public void RoundTripsNestedArrayOfDictionariesLikeStreamSetup()
    {
        PlistValue stream = new PlistDictBuilder()
            .Add("audioFormat", 0x40000L)
            .Add("shk", new byte[] { 1, 2, 3, 4, 5 })
            .Add("type", 0x60L)
            .Build();
        PlistValue root = new PlistDictBuilder()
            .Add("streams", PlistValue.Array([stream]))
            .Build();

        PlistValue? decoded = BinaryPlist.Decode(BinaryPlist.Encode(root));
        Assert.NotNull(decoded);
        PlistValue streams = decoded!.Find("streams")!;
        Assert.Equal(PlistValue.Kind.Array, streams.Type);
        Assert.Single(streams.ArrayValue);
        PlistValue s0 = streams.ArrayValue[0];
        Assert.Equal(0x40000L, s0.Find("audioFormat")!.AsInt());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, s0.Find("shk")!.DataValue);
        Assert.Equal(0x60L, s0.Find("type")!.AsInt());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(255L)]
    [InlineData(256L)]
    [InlineData(65535L)]
    [InlineData(65536L)]
    [InlineData(4294967295L)]
    [InlineData(4294967296L)]
    public void RoundTripsIntegersAcrossEveryWidthBoundary(long value)
    {
        PlistValue? decoded = BinaryPlist.Decode(BinaryPlist.Encode(PlistValue.Integer(value)));
        Assert.Equal(value, decoded!.AsInt());
    }

    [Fact]
    public void DecodeRejectsTruncatedGarbage()
    {
        Assert.Null(BinaryPlist.Decode([1, 2, 3]));
        Assert.Null(BinaryPlist.Decode(new byte[40])); // right size, wrong magic/trailer
    }

    [Fact]
    public void DecodeRejectsClaimedObjectCountLargerThanTheBuffer()
    {
        // A well-formed trailer but numObjects absurdly large must fail closed, not throw/hang/OOM.
        byte[] wire = BinaryPlist.Encode(PlistValue.Boolean(true));
        byte[] tampered = (byte[])wire.Clone();
        // numObjects lives at trailer offset (len-32+8), 8 bytes big-endian.
        int at = tampered.Length - 32 + 8;
        for (int i = 0; i < 8; i++) tampered[at + i] = 0xFF;
        Assert.Null(BinaryPlist.Decode(tampered));
    }
}
