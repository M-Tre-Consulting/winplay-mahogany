using AirPlaySender.Core.Tlv;
using Xunit;

namespace AirPlaySender.Core.Tests;

public class Tlv8Tests
{
    [Fact]
    public void RoundTripsSimpleValues()
    {
        var m = new Tlv8.Map();
        m.Add(Tlv8Type.State, 0x01);
        m.Add(Tlv8Type.Identifier, "hello"u8.ToArray());

        var decoded = Tlv8.Decode(Tlv8.Encode(m));

        Assert.Equal(new byte[] { 0x01 }, decoded.Get(Tlv8Type.State));
        Assert.Equal("hello"u8.ToArray(), decoded.Get(Tlv8Type.Identifier));
    }

    [Fact]
    public void FragmentsAndRejoinsValuesLargerThan255Bytes()
    {
        // The 384-byte SRP PublicKey is exactly the case this must handle.
        var value = new byte[384];
        for (int i = 0; i < value.Length; i++) value[i] = (byte)(i % 256);

        var m = new Tlv8.Map { { Tlv8Type.PublicKey, value } };
        byte[] wire = Tlv8.Encode(m);

        // Must be split into a 255-byte chunk + a 129-byte chunk, each with its own 2-byte header.
        Assert.Equal(2 + 255 + 2 + 129, wire.Length);

        var decoded = Tlv8.Decode(wire);
        Assert.Equal(value, decoded.Get(Tlv8Type.PublicKey));
    }

    [Fact]
    public void EmptyValueStillEmitsOneZeroLengthRecord()
    {
        var m = new Tlv8.Map { { Tlv8Type.Method, [] } };
        byte[] wire = Tlv8.Encode(m);
        Assert.Equal(new byte[] { (byte)Tlv8Type.Method, 0x00 }, wire);
    }

    [Fact]
    public void PreservesInsertionOrderOfDistinctTags()
    {
        var m = new Tlv8.Map { { Tlv8Type.State, 0x03 }, { Tlv8Type.PublicKey, [1, 2] }, { Tlv8Type.Proof, [9] } };
        var decoded = Tlv8.Decode(Tlv8.Encode(m));
        Assert.Equal([(byte)Tlv8Type.State, (byte)Tlv8Type.PublicKey, (byte)Tlv8Type.Proof], decoded.Select(kv => kv.Key));
    }
}
