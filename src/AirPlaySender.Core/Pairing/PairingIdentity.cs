using System.Text;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Pairing;

/// <summary>
/// This sender's own long-term HAP identity (pyatv "ltsk"/"ltpk") plus a
/// stable pairing id (a UUID, HAP's "Identifier" TLV for us). Generated
/// once and reused for every device — HAP identifies the CONTROLLER by
/// this key pair, not by anything per-accessory.
/// </summary>
public sealed class PairingIdentity
{
    // Not `required` — see AirPlayDevice's doc comment (XamlCompiler.exe + `required`).
    public byte[] Seed32 { get; init; } = [];
    public byte[] PairingId { get; init; } = [];

    private byte[]? _publicKey;
    public byte[] PublicKey32 => _publicKey ??= Ed25519Signer.PublicFromSeed(Seed32);

    public static PairingIdentity CreateNew() => new()
    {
        Seed32 = Ed25519Signer.GenerateSeed(),
        PairingId = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D").ToUpperInvariant()),
    };

    public string SeedHex => Convert.ToHexString(Seed32);
    public string PairingIdText => Encoding.UTF8.GetString(PairingId);

    public static PairingIdentity FromStorage(string seedHex, string pairingIdText) => new()
    {
        Seed32 = Convert.FromHexString(seedHex),
        PairingId = Encoding.UTF8.GetBytes(pairingIdText),
    };
}
