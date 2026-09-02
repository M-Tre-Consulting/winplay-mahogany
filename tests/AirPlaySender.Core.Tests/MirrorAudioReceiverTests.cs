using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using AirPlaySender.Core.Receiving;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// Drives a real <see cref="MirrorAudioReceiver"/> over a loopback UDP socket
/// with hand-built RTP audio packets. Pins:
/// <list type="bullet">
/// <item>12-byte RTP header, sequence number at bytes 2-3 big-endian;</item>
/// <item>payload is AES-128-CBC (key + fixed IV, whole 16-byte blocks only,
/// trailing bytes plaintext) — the decrypted bytes are handed out verbatim;</item>
/// <item>packets are emitted in sequence-number order, and a redundant re-send
/// (a packet whose seq is already past) is dropped, not decoded twice.</item>
/// </list>
/// </summary>
public class MirrorAudioReceiverTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 2)).ToArray();
    private static readonly byte[] Iv = Enumerable.Range(0, 16).Select(i => (byte)(0x11 * i & 0xFF)).ToArray();

    private static byte[] RtpAudioPacket(ushort seq, byte[] clearPayload)
    {
        var pkt = new byte[12 + clearPayload.Length];
        pkt[0] = 0x80;            // V=2
        pkt[1] = 0x60;            // M=0, PT=96
        pkt[2] = (byte)(seq >> 8);
        pkt[3] = (byte)seq;

        int encLen = clearPayload.Length & ~0xF;
        using Aes aes = Aes.Create();
        aes.Key = Key; aes.IV = Iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
        if (encLen > 0)
        {
            using ICryptoTransform enc = aes.CreateEncryptor();
            enc.TransformBlock(clearPayload, 0, encLen, pkt, 12);
        }
        Array.Copy(clearPayload, encLen, pkt, 12 + encLen, clearPayload.Length - encLen); // tail plaintext
        return pkt;
    }

    [Fact]
    public async Task DecryptsReordersAndDropsRedundantResends()
    {
        await using var receiver = new MirrorAudioReceiver(Key, Iv);

        var frames = new List<byte[]>();
        var got = new SemaphoreSlim(0);
        receiver.AudioFrameReceived += f => { lock (frames) frames.Add(f); got.Release(); };
        receiver.Start();

        using var client = new UdpClient();
        var dst = new IPEndPoint(IPAddress.Loopback, receiver.DataPort);

        // 30-byte payloads (first byte a real AAC-ELD marker) — 16 encrypted + 14 plaintext tail.
        byte[] Payload(byte tag) => [.. new byte[] { tag }, .. Enumerable.Range(1, 29).Select(i => (byte)i)];
        byte[] p10 = Payload(0x8c), p11 = Payload(0x8d), p12 = Payload(0x8e);

        await client.SendAsync(RtpAudioPacket(10, p10), dst);
        await client.SendAsync(RtpAudioPacket(12, p12), dst); // out of order — should be buffered
        await client.SendAsync(RtpAudioPacket(11, p11), dst); // fills the gap -> 11 then 12 drain
        await client.SendAsync(RtpAudioPacket(10, p10), dst); // redundant re-send of an old seq -> dropped

        for (int i = 0; i < 3; i++)
            Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)), $"frame {i} non arrivato");
        await Task.Delay(100); // give a wrongly-accepted 4th frame a chance to show

        lock (frames)
        {
            Assert.Equal(3, frames.Count);
            Assert.Equal(p10, frames[0]);
            Assert.Equal(p11, frames[1]);
            Assert.Equal(p12, frames[2]);
        }
    }
}
