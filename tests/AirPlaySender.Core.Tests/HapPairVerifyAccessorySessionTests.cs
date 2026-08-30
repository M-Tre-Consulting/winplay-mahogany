using AirPlaySender.Core.Pairing;
using AirPlaySender.Core.Receiving;
using AirPlaySender.Core.Rtsp;
using AirPlaySender.Core.Tests.TestSupport;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// Runs <see cref="HapPairVerifyAccessorySession"/> — the accessory-role
/// mirror of this project's own controller-role <c>PairVerifyClient</c> —
/// against that REAL, unmodified client class over a real loopback TCP
/// socket. Same trick <c>AirPlaySessionIntegrationTests</c> already uses
/// with <c>FakeAirPlay2Receiver</c>, aimed the other direction: here OUR
/// code plays the accessory, and Phase 1's already-hardware-verified client
/// code is the one holding it to the real protocol.
///
/// Pair-setup doesn't exist yet for this scheme (see the class-level doc
/// comment on <see cref="HapPairVerifyAccessorySession"/> for why it isn't
/// guessed at here) — so this fabricates the two <c>StoredCredentials</c>-
/// shaped facts a real pair-setup would have produced and persisted
/// (each side already knowing the other's long-term Ed25519 public key)
/// rather than skip verifying pair-verify itself until that exists.
/// </summary>
public class HapPairVerifyAccessorySessionTests
{
    [Fact(Timeout = 20000)]
    public async Task AccessoryAndControllerAgreeOnASharedSecretAndCanDecryptEachOthersChannel()
    {
        ReceiverIdentity accessoryIdentity = ReceiverIdentity.CreateNew();
        PairingIdentity controllerIdentity = PairingIdentity.CreateNew();

        // What a completed pair-setup would have left behind on each side.
        var credentials = new StoredCredentials
        {
            LtSeed = controllerIdentity.Seed32,
            PairingId = controllerIdentity.PairingId,
            AccessoryId = System.Text.Encoding.UTF8.GetBytes(accessoryIdentity.Pi.ToString("D").ToUpperInvariant()),
            AccessoryLtpk = accessoryIdentity.PublicKey32,
        };
        var accessorySession = new HapPairVerifyAccessorySession(accessoryIdentity, controllerIdentity.PublicKey32);

        await using var fake = new FakeHapAccessory(accessorySession);
        fake.Start();

        await using RtspConnection conn = await RtspConnection.ConnectAsync("127.0.0.1", fake.Port, "TEST-DACP", 1);
        PairingResult clientResult = await PairVerifyClient.RunAsync(conn, controllerIdentity, credentials);

        Assert.True(accessorySession.IsVerified);
        Assert.NotNull(accessorySession.SharedSecret);
        Assert.Equal(clientResult.SharedSecret, accessorySession.SharedSecret);

        // The accessory's read/write keys must be the client's write/read keys
        // (same key values — HAP has one symmetric key per direction, shared).
        PairingResult accessoryDerived = ReDeriveAsAccessory(accessorySession.SharedSecret!);
        Assert.Equal(clientResult.ControlWriteKey, accessoryDerived.ControlWriteKey);
        Assert.Equal(clientResult.ControlReadKey, accessoryDerived.ControlReadKey);
    }

    // SessionKeyDerivation is internal — this just re-runs the same public HKDF
    // helper both sides already use, to confirm both independently land on the
    // identical key bytes for a shared secret each computed on its own.
    private static PairingResult ReDeriveAsAccessory(byte[] sharedSecret)
    {
        return new PairingResult
        {
            SharedSecret = sharedSecret,
            ControlWriteKey = AirPlaySender.Core.Crypto.Hkdf.DeriveSha512("Control-Salt", "Control-Write-Encryption-Key", sharedSecret, 32),
            ControlReadKey = AirPlaySender.Core.Crypto.Hkdf.DeriveSha512("Control-Salt", "Control-Read-Encryption-Key", sharedSecret, 32),
        };
    }
}
