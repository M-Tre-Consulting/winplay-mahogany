using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AirPlaySender.Core.Audio;
using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Receiving;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// Drives a real <see cref="MirroringDataReceiver"/> over a loopback TCP
/// connection with hand-built mirroring packets (128-byte header + payload,
/// AES-CTR encrypted video, unencrypted AVCDecoderConfigurationRecord) — the
/// same "test against a real socket, not a mock" approach the rest of Phase 2
/// uses. Pins the frame-assembly contract the renderer depends on:
/// <list type="bullet">
/// <item>each video packet becomes one whole access unit, AVCC 4-byte length
/// prefixes swapped for Annex-B <c>00 00 00 01</c> start codes;</item>
/// <item>SPS/PPS is prepended to every key frame, so a decoder can start (or
/// recover) from any IDR without having seen the config packet;</item>
/// <item>the per-frame timestamp is read from header offset 8 as a little-endian
/// NTP 64-bit fixed-point value and converted to whole nanoseconds;</item>
/// <item>frames before the first key frame are dropped (a decoder cannot
/// start on a P-frame).</item>
/// </list>
/// </summary>
public class MirroringDataReceiverVideoTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)(i * 5 + 1)).ToArray();
    private static readonly byte[] Iv = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

    // version=1, profile=0x64, compat=0x00, level=0x1E, 0xFF, 0xE1 (1 SPS),
    // SPS len 3 {0x67,0xAA,0xBB}, 1 PPS, PPS len 2 {0x68,0xCC}.
    private static readonly byte[] ConfigRecord = Convert.FromHexString("0164001EFFE1000367AABB01000268CC");
    private static readonly byte[] Sps = [0x67, 0xAA, 0xBB];
    private static readonly byte[] Pps = [0x68, 0xCC];
    private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

    // header offset 8 carries the frame time as an NTP 32.32 fixed-point value.
    private const ulong Ntp7s = 0x0000_0007_0000_0000UL;   // 7.0s  -> 7_000_000_000 ns
    private const ulong Ntp7_5s = 0x0000_0007_8000_0000UL; // 7.5s  -> 7_500_000_000 ns
    private const ulong Ntp1s = 0x0000_0001_0000_0000UL;   // 1.0s
    private const ulong Ntp1_25s = 0x0000_0001_4000_0000UL; // 1.25s -> 1_250_000_000 ns

    private static byte[] Header(byte type, byte subtype, int payloadSize, ulong ntpTimestamp)
    {
        var h = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(0, 4), payloadSize);
        h[4] = type;
        h[5] = subtype;
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8, 8), ntpTimestamp);
        return h;
    }

    private static byte[] Avcc(params byte[][] nals)
    {
        var outp = new List<byte>();
        foreach (byte[] n in nals)
        {
            outp.Add((byte)(n.Length >> 24));
            outp.Add((byte)(n.Length >> 16));
            outp.Add((byte)(n.Length >> 8));
            outp.Add((byte)n.Length);
            outp.AddRange(n);
        }
        return [.. outp];
    }

    [Fact]
    public void NtpToNanosecondsMatchesHandComputedVectors()
    {
        Assert.Equal(7_000_000_000UL, Ntp.ToNanoseconds(Ntp7s));
        Assert.Equal(7_500_000_000UL, Ntp.ToNanoseconds(Ntp7_5s));
        Assert.Equal(1_250_000_000UL, Ntp.ToNanoseconds(Ntp1_25s));
        Assert.Equal(0UL, Ntp.ToNanoseconds(0));
    }

    [Fact]
    public async Task AssemblesAnnexBAccessUnitsWithSpsPpsOnKeyFramesAndTheHeaderTimestamp()
    {
        await using var receiver = new MirroringDataReceiver();
        receiver.SetVideoKeyIv(Key, Iv);

        (byte[] sps, byte[] pps)? config = null;
        var frames = new List<MirroringVideoFrame>();
        var got = new SemaphoreSlim(0);
        receiver.ConfigReceived += (s, p) => config = (s, p);
        receiver.FrameReceived += f => { frames.Add(f); got.Release(); };
        receiver.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.LocalPort);
        NetworkStream ns = client.GetStream();

        // One continuous AES-CTR keystream across the encrypted (type 0) payloads only —
        // the config packet is not encrypted and must not advance it.
        var cipher = new AesCtrKeystreamCipher(Key, Iv);

        await ns.WriteAsync(Header(0x01, 0x00, ConfigRecord.Length, 0));
        await ns.WriteAsync(ConfigRecord);

        byte[] idr = Avcc([0x65, 0x11, 0x22, 0x33]);        // NAL type 5 = IDR slice
        byte[] idrEnc = cipher.Transform(idr);
        await ns.WriteAsync(Header(0x00, 0x10, idrEnc.Length, Ntp7s));
        await ns.WriteAsync(idrEnc);

        byte[] p = Avcc([0x41, 0x44, 0x55]);                // NAL type 1 = non-IDR slice
        byte[] pEnc = cipher.Transform(p);
        await ns.WriteAsync(Header(0x00, 0x00, pEnc.Length, Ntp7_5s));
        await ns.WriteAsync(pEnc);

        Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.NotNull(config);
        Assert.Equal(Sps, config!.Value.sps);
        Assert.Equal(Pps, config.Value.pps);

        Assert.Equal(2, frames.Count);

        // IDR: SPS + PPS prepended, then the slice — all Annex-B framed.
        byte[] expectedIdr = [.. StartCode, .. Sps, .. StartCode, .. Pps, .. StartCode, 0x65, 0x11, 0x22, 0x33];
        Assert.Equal(expectedIdr, frames[0].AnnexB);
        Assert.True(frames[0].IsKeyFrame);
        Assert.Equal(7_000_000_000UL, frames[0].TimestampNs);

        // P-frame: no parameter sets, just the slice.
        byte[] expectedP = [.. StartCode, 0x41, 0x44, 0x55];
        Assert.Equal(expectedP, frames[1].AnnexB);
        Assert.False(frames[1].IsKeyFrame);
        Assert.Equal(7_500_000_000UL, frames[1].TimestampNs);
    }

    [Fact]
    public async Task ReplaysConfigAndFramesSinceTheKeyFrameToARendererThatAttachesLate()
    {
        await using var receiver = new MirroringDataReceiver();
        receiver.SetVideoKeyIv(Key, Iv);

        int packetsSeen = 0;
        var fourSeen = new SemaphoreSlim(0);
        receiver.Diagnostics += m => { if (m.StartsWith("Pacchetto #") && Interlocked.Increment(ref packetsSeen) >= 4) fourSeen.Release(); };
        receiver.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.LocalPort);
        NetworkStream ns = client.GetStream();
        var cipher = new AesCtrKeystreamCipher(Key, Iv);

        // config + IDR + two P-frames, all before anyone subscribes.
        await ns.WriteAsync(Header(0x01, 0x00, ConfigRecord.Length, 0));
        await ns.WriteAsync(ConfigRecord);
        byte[] idr = cipher.Transform(Avcc([0x65, 0x01]));
        await ns.WriteAsync(Header(0x00, 0x10, idr.Length, Ntp7s));
        await ns.WriteAsync(idr);
        byte[] p1 = cipher.Transform(Avcc([0x41, 0x02]));
        await ns.WriteAsync(Header(0x00, 0x00, p1.Length, Ntp7_5s));
        await ns.WriteAsync(p1);
        byte[] p2 = cipher.Transform(Avcc([0x41, 0x03]));
        await ns.WriteAsync(Header(0x00, 0x00, p2.Length, 0x0000_0008_0000_0000UL)); // 8.0s
        await ns.WriteAsync(p2);

        Assert.True(await fourSeen.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(50); // let the 4th payload finish emitting

        // Now attach — everything already happened with no subscriber.
        (byte[] sps, byte[] pps)? config = null;
        var frames = new List<MirroringVideoFrame>();
        receiver.AttachRenderer((s, p) => config = (s, p), frames.Add, () => { });

        Assert.NotNull(config);
        Assert.Equal(Sps, config!.Value.sps);
        Assert.Equal(3, frames.Count);                 // IDR + 2 P-frames since it
        Assert.True(frames[0].IsKeyFrame);
        Assert.Equal(7_000_000_000UL, frames[0].TimestampNs);
        Assert.False(frames[1].IsKeyFrame);
        Assert.False(frames[2].IsKeyFrame);
    }

    [Fact]
    public async Task DropsFramesArrivingBeforeTheFirstKeyFrame()
    {
        await using var receiver = new MirroringDataReceiver();
        receiver.SetVideoKeyIv(Key, Iv);

        var frames = new List<MirroringVideoFrame>();
        var got = new SemaphoreSlim(0);
        receiver.FrameReceived += f => { frames.Add(f); got.Release(); };
        receiver.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.LocalPort);
        NetworkStream ns = client.GetStream();
        var cipher = new AesCtrKeystreamCipher(Key, Iv);

        // A non-IDR slice with no key frame ever seen — must be dropped.
        byte[] p = cipher.Transform(Avcc([0x41, 0x01, 0x02]));
        await ns.WriteAsync(Header(0x00, 0x00, p.Length, Ntp1s));
        await ns.WriteAsync(p);

        // Then a real IDR — must come through.
        byte[] idr = cipher.Transform(Avcc([0x65, 0x03, 0x04]));
        await ns.WriteAsync(Header(0x00, 0x10, idr.Length, Ntp1_25s));
        await ns.WriteAsync(idr);

        Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)));
        // Give any wrongly-accepted earlier frame a chance to have shown up too.
        await Task.Delay(100);

        Assert.Single(frames);
        Assert.True(frames[0].IsKeyFrame);
        Assert.Equal(1_250_000_000UL, frames[0].TimestampNs);
    }
}
