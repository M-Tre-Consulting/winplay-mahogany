using System.Buffers.Binary;
using AirPlaySender.Core.Receiving;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// <see cref="NtpTimingSession.BuildRequestPacket"/> is the wire format read
/// from UxPlay's <c>raop_ntp_thread</c>/<c>byteutils_put_ntp_timestamp</c> —
/// verified against real hardware (the iPhone replies to it), but that's a
/// one-off manual check, not something that keeps failing loudly if someone
/// edits an offset by accident later. These pin the exact byte layout down.
/// </summary>
public class NtpTimingSessionTests
{
    private const ulong SecondsFrom1900To1970 = 2208988800UL;

    [Fact]
    public void IsAlways32BytesWithTheFixedHeader()
    {
        byte[] packet = NtpTimingSession.BuildRequestPacket(sendTimeNs: 0, clientRefTimeRaw: null, lastLocalRecvNs: null);

        Assert.Equal(32, packet.Length);
        Assert.Equal(new byte[] { 0x80, 0xd2, 0x00, 0x07 }, packet[..4]);
        Assert.Equal(new byte[4], packet[4..8]); // reserved, always zero
    }

    [Fact]
    public void FirstRequestOfASessionLeavesOriginAndReceiveTimestampsZero()
    {
        // No prior response yet — matches raop_ntp_thread before its first recv_time exists.
        byte[] packet = NtpTimingSession.BuildRequestPacket(sendTimeNs: 1_000_000_000, clientRefTimeRaw: null, lastLocalRecvNs: null);

        Assert.Equal(new byte[8], packet[8..16]);  // origin timestamp
        Assert.Equal(new byte[8], packet[16..24]); // receive timestamp
        Assert.NotEqual(new byte[8], packet[24..32]); // transmit timestamp IS set
    }

    [Fact]
    public void EchoesTheClientReferenceTimeVerbatimNotReinterpreted()
    {
        // byteutils_get_long_be(response,24) copied straight into byteutils_put_long_be(request,8,...) —
        // a raw 8-byte passthrough, not decoded/re-encoded as an NTP timestamp.
        ulong rawClientValue = 0x1122334455667788UL;
        byte[] packet = NtpTimingSession.BuildRequestPacket(sendTimeNs: 0, clientRefTimeRaw: rawClientValue, lastLocalRecvNs: 0);

        Assert.Equal(rawClientValue, BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8, 8)));
    }

    [Fact]
    public void EncodesWholeSecondsWithZeroFraction()
    {
        // 2024-01-01T00:00:00Z, a round second: fraction must be exactly zero,
        // and the NTP seconds field is Unix seconds + the 1900->1970 epoch offset.
        const ulong unixSeconds = 1704067200UL;
        const ulong nsSince1970 = unixSeconds * 1_000_000_000UL;

        byte[] packet = NtpTimingSession.BuildRequestPacket(nsSince1970, clientRefTimeRaw: null, lastLocalRecvNs: null);

        uint ntpSeconds = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(24, 4));
        uint fraction = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(28, 4));
        Assert.Equal((uint)(unixSeconds + SecondsFrom1900To1970), ntpSeconds);
        Assert.Equal(0u, fraction);
    }

    [Fact]
    public void EncodesHalfASecondAsExactlyHalfTheFractionRange()
    {
        // 500_000_000ns is precisely half a second: fraction = (ns << 32) / 1e9 = 2^31 = 0x80000000 exactly.
        byte[] packet = NtpTimingSession.BuildRequestPacket(sendTimeNs: 500_000_000UL, clientRefTimeRaw: null, lastLocalRecvNs: null);

        uint fraction = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(28, 4));
        Assert.Equal(0x8000_0000u, fraction);
    }

    [Fact]
    public void SubsequentRequestFillsInOriginAndReceiveTimestamps()
    {
        byte[] packet = NtpTimingSession.BuildRequestPacket(
            sendTimeNs: 2_000_000_000UL,
            clientRefTimeRaw: 0xAABBCCDDEEFF0011UL,
            lastLocalRecvNs: 1_500_000_000UL);

        Assert.NotEqual(new byte[8], packet[8..16]);  // origin timestamp now set
        Assert.NotEqual(new byte[8], packet[16..24]); // receive timestamp now set
    }
}
