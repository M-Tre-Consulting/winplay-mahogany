using System.Text.Json;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// This machine's own long-term identity when acting as an AirPlay
/// *receiver* (mirroring target) — the accessory side of HAP, the mirror
/// image of <see cref="Pairing.PairingIdentity"/> which is our identity as
/// a *controller*. Deliberately a separate key pair and a separate file
/// from the sender's credentials.json: the two roles are architecturally
/// unrelated (a real HomePod and a real Apple TV don't share a key either),
/// and mixing them would make it harder to reason about which identity is
/// being presented to whom.
///
/// <c>Pi</c> is HAP's accessory "pairing identifier" — advertised in the
/// clear in the mDNS TXT record (key "pi") so a controller can recognize
/// this accessory across pairings. Generated once per install and persisted,
/// unlike the reference implementation (UxPlay) which hardcodes the same
/// GUID for every install of the software — fine for a single hobbyist
/// build, but the wrong choice for anything meant to be its own distinct
/// accessory on the network.
/// </summary>
public sealed class ReceiverIdentity
{
    public byte[] Seed32 { get; init; } = [];
    public Guid Pi { get; init; }

    private byte[]? _publicKey;
    public byte[] PublicKey32 => _publicKey ??= Ed25519Signer.PublicFromSeed(Seed32);
    public string PublicKeyHex => Convert.ToHexString(PublicKey32).ToLowerInvariant();

    public static ReceiverIdentity CreateNew() => new()
    {
        Seed32 = Ed25519Signer.GenerateSeed(),
        Pi = Guid.NewGuid(),
    };

    /// <summary>Loads the persisted identity, creating and saving a new one on first run.</summary>
    public static ReceiverIdentity LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                Stored? stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
                if (stored is { Seed32Hex.Length: > 0 })
                {
                    return new ReceiverIdentity
                    {
                        Seed32 = Convert.FromHexString(stored.Seed32Hex),
                        Pi = Guid.Parse(stored.Pi),
                    };
                }
            }
        }
        catch { /* a corrupt file just means "generate a new identity", not a crash */ }

        ReceiverIdentity created = CreateNew();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(new Stored
            {
                Seed32Hex = Convert.ToHexString(created.Seed32),
                Pi = created.Pi.ToString(),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort persistence — losing it just means a new identity next run */ }
        return created;
    }

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AirPlayForWindows", "receiver-identity.json");

    private sealed class Stored
    {
        public string Seed32Hex { get; set; } = "";
        public string Pi { get; set; } = "";
    }
}
