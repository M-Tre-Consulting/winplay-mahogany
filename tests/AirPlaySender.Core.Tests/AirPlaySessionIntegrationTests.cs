using System.Buffers.Binary;
using AirPlaySender.Core.Audio;
using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Discovery;
using AirPlaySender.Core.Pairing;
using AirPlaySender.Core.Tests.TestSupport;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// End-to-end tests running <see cref="AirPlaySession"/> against
/// <see cref="FakeAirPlay2Receiver"/> — a real TCP/UDP round trip over
/// loopback, with the fake receiver computing its half of SRP-6a
/// independently. This is the strongest check available without real Apple
/// hardware: it proves the client can complete an entire AirPlay-2
/// transient-pairing handshake and produce a correctly-encrypted RTP audio
/// packet that decrypts back to the exact bytes that went in.
/// </summary>
public class AirPlaySessionIntegrationTests
{
    // Bit 48 (SupportsCoreUtilsPairingAndEncryption): selects BOTH transient
    // pairing (no PIN) and AirPlay-2 detection in one flag, exactly what a
    // HomePod/macOS-class receiver advertises.
    private const string TransientAirPlay2Features = "0x00000000,0x00010000";

    [Fact(Timeout = 20000)]
    public async Task CompletesTransientPairingAndStreamsAVerifiableAudioPacket()
    {
        await using var receiver = new FakeAirPlay2Receiver(pin: "3939");
        receiver.Start();

        var device = new AirPlayDevice
        {
            Name = "Fake Receiver",
            Host = "127.0.0.1",
            Port = receiver.Port,
            DeviceId = "AA:BB:CC:DD:EE:FF@Fake Receiver",
            Properties = new Dictionary<string, string> { ["features"] = TransientAirPlay2Features },
        };
        Assert.Equal(AirPlayAuthMethod.HapTransient, device.DetermineAuthMethod());
        Assert.True(device.IsAirPlay2);

        string credsPath = Path.Combine(Path.GetTempPath(), $"airplay-test-{Guid.NewGuid():N}.json");
        var fakeSource = new FakePcmFrameSource();
        await using var session = new AirPlaySession(new CredentialStore(credsPath), audioSourceFactory: () => fakeSource);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.ConnectAsync(device, cts.Token);

        Assert.Equal(AirPlaySessionState.Streaming, session.State);
        Assert.True(fakeSource.Started);

        byte[] packet = await receiver.AudioPacketReceived.WaitAsync(TimeSpan.FromSeconds(5));

        // RTP header sanity: version 2 / no padding / no extension / CC=0; marker set + payload type 0x60 on the first packet.
        Assert.Equal(0x80, packet[0]);
        Assert.Equal(0xE0, packet[1]);
        uint rtpTimestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
        Assert.Equal(22050u + 44100u, rtpTimestamp); // fixed RAOP latency, first packet (framesSent == 0)

        Assert.NotNull(receiver.AudioKey);
        ReadOnlySpan<byte> header = packet.AsSpan(0, 12);
        ReadOnlySpan<byte> wirePayload = packet.AsSpan(12);
        byte[] nonce = wirePayload[^8..].ToArray();
        byte[] ciphertextAndTag = wirePayload[..^8].ToArray();
        byte[] aad = header[4..12].ToArray();

        byte[]? decrypted = ChaCha20Poly1305Cipher.TryDecrypt(receiver.AudioKey!, nonce, ciphertextAndTag, aad);
        Assert.NotNull(decrypted);

        // Raw big-endian L16 PCM (RFC 3551 payload type 96), not ALAC — confirmed
        // against a real HomePod as the encoding that actually gets accepted;
        // see the comment on RtpAudioTransport.SendAudioPacket for the story.
        var expectedPayload = new byte[RtpAudioTransport.FramesPerPacket * 2 * 2];
        for (int i = 0; i < RtpAudioTransport.FramesPerPacket * 2; i++)
            BinaryPrimitives.WriteInt16BigEndian(expectedPayload.AsSpan(i * 2), FakePcmFrameSource.SampleValue);
        Assert.Equal(expectedPayload, decrypted);
    }
}
