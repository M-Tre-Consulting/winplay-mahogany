using AirPlaySender.Core.Pairing;
using AirPlaySender.Core.Receiving;
using AirPlaySender.Core.Rtsp;
using AirPlaySender.Core.Tests.TestSupport;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// Runs <see cref="HapPairSetupAccessorySession"/> (transient shape) against
/// this project's own, unmodified <c>Pairing.PairSetupClient.RunTransientAsync</c>
/// over a real loopback TCP socket — same trick as
/// <see cref="HapPairVerifyAccessorySessionTests"/>. This is a documented
/// GUESS at mirroring's pair-setup shape (see the class-level doc comment on
/// <see cref="HapPairSetupAccessorySession"/>): this test proves the SRP-6a
/// math is correct against a real, independent implementation of the
/// controller side — it does NOT prove a real iPhone actually speaks this
/// shape for mirroring, which remains unverified against real hardware.
/// </summary>
public class HapPairSetupAccessorySessionTests
{
    [Fact(Timeout = 20000)]
    public async Task AccessoryAndControllerAgreeOnASessionKey()
    {
        var accessorySession = new HapPairSetupAccessorySession();

        await using var fake = new FakeHapPairSetupAccessory(accessorySession);
        fake.Start();

        await using RtspConnection conn = await RtspConnection.ConnectAsync("127.0.0.1", fake.Port, "TEST-DACP", 1);
        PairingResult clientResult = await PairSetupClient.RunTransientAsync(conn);

        Assert.True(accessorySession.IsComplete);
        Assert.NotNull(accessorySession.SessionKey);
        Assert.Equal(clientResult.SharedSecret, accessorySession.SessionKey);

        // Accessory decrypts with the client's "write" key and encrypts with
        // the client's "read" key — same key values, roles swapped.
        byte[] accessoryDecryptKey = AirPlaySender.Core.Crypto.Hkdf.DeriveSha512("Control-Salt", "Control-Write-Encryption-Key", accessorySession.SessionKey!, 32);
        byte[] accessoryEncryptKey = AirPlaySender.Core.Crypto.Hkdf.DeriveSha512("Control-Salt", "Control-Read-Encryption-Key", accessorySession.SessionKey!, 32);
        Assert.Equal(clientResult.ControlWriteKey, accessoryDecryptKey);
        Assert.Equal(clientResult.ControlReadKey, accessoryEncryptKey);
    }

    [Fact(Timeout = 20000)]
    public async Task RejectsAPinFlowM1WithNoTransientFlag()
    {
        var accessorySession = new HapPairSetupAccessorySession();
        var m1 = new AirPlaySender.Core.Tlv.Tlv8.Map();
        m1.Add(AirPlaySender.Core.Tlv.Tlv8Type.Method, 0x00);
        m1.Add(AirPlaySender.Core.Tlv.Tlv8Type.State, 0x01);
        // No Flags TLV at all — the PIN-flow shape.
        byte[]? resp = accessorySession.Handle(AirPlaySender.Core.Tlv.Tlv8.Encode(m1));
        Assert.Null(resp);
        await Task.CompletedTask;
    }
}
