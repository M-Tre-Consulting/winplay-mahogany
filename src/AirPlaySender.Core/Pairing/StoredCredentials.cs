using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirPlaySender.Core.Pairing;

/// <summary>
/// The result of a completed on-screen-PIN HAP pair-setup: the accessory's
/// long-term identity, kept so later connects run pair-verify only and
/// never ask for the PIN again.
/// </summary>
public sealed class StoredCredentials
{
    // Not `required` — see AirPlayDevice's doc comment (XamlCompiler.exe + `required`).
    public byte[] LtSeed { get; init; } = [];      // our OWN long-term seed used for this pairing (PairingIdentity.Seed32)
    public byte[] PairingId { get; init; } = [];    // our OWN pairing id (PairingIdentity.PairingId)
    public byte[] AccessoryId { get; init; } = [];  // the receiver's HAP "Identifier"
    public byte[] AccessoryLtpk { get; init; } = []; // the receiver's long-term Ed25519 public key
}

/// <summary>Hex-encoded JSON persistence for <see cref="StoredCredentials"/>, one file per install, keyed by AirPlayDevice.DeviceId.</summary>
public sealed class CredentialStore
{
    private sealed class Entry
    {
        public string LtSeed { get; set; } = "";
        public string PairingId { get; set; } = "";
        public string AccessoryId { get; set; } = "";
        public string AccessoryLtpk { get; set; } = "";
    }

    private readonly string _path;
    private readonly string? _legacyPath; // pre-rename location; read once so an upgrade doesn't lose saved pairings
    private Dictionary<string, Entry> _entries = new();

    public CredentialStore(string? path = null)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = path ?? Path.Combine(localAppData, "WinPlayMahogany", "credentials.json");
        _legacyPath = path is null ? Path.Combine(localAppData, "AirPlayForWindows", "credentials.json") : null;
        Load();
    }

    private void Load()
    {
        try
        {
            string src = File.Exists(_path) ? _path
                       : _legacyPath is not null && File.Exists(_legacyPath) ? _legacyPath
                       : _path;
            if (File.Exists(src))
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(src))
                           ?? new Dictionary<string, Entry>();
        }
        catch
        {
            _entries = new Dictionary<string, Entry>(); // a corrupt store just means "pair again", not a crash
        }
    }

    private void Save()
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, s_jsonOptions));
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public StoredCredentials? Get(string deviceId)
    {
        if (!_entries.TryGetValue(deviceId, out Entry? e)) return null;
        return new StoredCredentials
        {
            LtSeed = Convert.FromHexString(e.LtSeed),
            PairingId = Convert.FromHexString(e.PairingId),
            AccessoryId = Convert.FromHexString(e.AccessoryId),
            AccessoryLtpk = Convert.FromHexString(e.AccessoryLtpk),
        };
    }

    public void Set(string deviceId, StoredCredentials credentials)
    {
        _entries[deviceId] = new Entry
        {
            LtSeed = Convert.ToHexString(credentials.LtSeed),
            PairingId = Convert.ToHexString(credentials.PairingId),
            AccessoryId = Convert.ToHexString(credentials.AccessoryId),
            AccessoryLtpk = Convert.ToHexString(credentials.AccessoryLtpk),
        };
        Save();
    }

    public void Remove(string deviceId)
    {
        if (_entries.Remove(deviceId)) Save();
    }
}
