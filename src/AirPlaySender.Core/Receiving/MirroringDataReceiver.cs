using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The TCP connection a real device opens after a successful mirroring
/// <c>SETUP</c> — this is where the actual H.264 video arrives. Not a full
/// player: this milestone proves the crypto chain end to end (pairing →
/// FairPlay → per-stream AES-CTR key) by decrypting real frames and
/// checking for a well-formed H.264 NAL start code, and logs what it sees.
/// Decode/render is the next, separate milestone.
///
/// Packet framing (128-byte header + payload) and the AES-CTR video key/iv
/// derivation are taken from UxPlay's <c>lib/raop_rtp_mirror.c</c>
/// (<c>raop_rtp_mirror_thread</c>, <c>mirror_buffer_init_aes</c>) — see the
/// doc comment on <see cref="AirPlayMirroringAdvertiser"/> for this
/// project's attribution convention.
/// </summary>
public sealed class MirroringDataReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public event Action<string>? Diagnostics;
    public int LocalPort { get; }

    public MirroringDataReceiver()
    {
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start() => _acceptLoop = AcceptLoopAsync(_cts.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            Trace($"Connessione dati mirroring accettata da {client.Client.RemoteEndPoint}");
            await ReadPacketsAsync(client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Trace($"Errore sul canale dati mirroring: {ex.Message}"); }
    }

    /// <summary>Derives the per-stream video AES-CTR key/iv: SHA-512("AirPlayStreamKey{id}" or "...IV{id}" || sessionAesKey)[0..16].</summary>
    public static (byte[] Key, byte[] Iv) DeriveVideoKeyIv(byte[] sessionAesKey, long streamConnectionId)
    {
        byte[] key = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamKey{streamConnectionId}"), .. sessionAesKey])[..16];
        byte[] iv = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamIV{streamConnectionId}"), .. sessionAesKey])[..16];
        return (key, iv);
    }

    private async Task ReadPacketsAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream stream = client.GetStream();
        var header = new byte[128];
        int packetCount = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, header, 128, ct).ConfigureAwait(false))
            {
                Trace("Canale dati mirroring chiuso dal peer");
                return;
            }

            int payloadSize = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            byte payloadTypeHigh = header[4], payloadTypeLow = header[5];
            if (payloadSize is < 0 or > 8 * 1024 * 1024)
            {
                Trace($"Header mirroring con payloadSize implausibile ({payloadSize}), mi fermo");
                return;
            }

            var payload = new byte[payloadSize];
            if (!await ReadExactAsync(stream, payload, payloadSize, ct).ConfigureAwait(false))
            {
                Trace("Canale dati mirroring chiuso a metà payload");
                return;
            }

            packetCount++;
            string kind = (payloadTypeHigh, payloadTypeLow) switch
            {
                (0x00, 0x00) => "video (non-IDR, cifrato)",
                (0x00, 0x10) => "video (IDR, cifrato)",
                (0x01, 0x00) => "SPS+PPS (non cifrato)",
                (0x02, 0x00) => "keepalive",
                (0x05, 0x00) => "streaming report",
                _ => $"tipo sconosciuto 0x{payloadTypeHigh:X2}{payloadTypeLow:X2}",
            };
            Trace($"Pacchetto #{packetCount}: {kind}, {payloadSize} byte");

            if (payloadTypeHigh == 0x01)
            {
                // Unencrypted SPS+PPS — if this really is well-formed H.264, it starts with a NAL start code immediately.
                LogNalStartCode(payload, "SPS/PPS");
            }
            else if (payloadTypeHigh == 0x00 && payloadSize > 0)
            {
                byte[] decrypted = _videoCipher!.Transform(payload);
                LogNalStartCode(decrypted, "VCL NAL decifrato");
            }
        }
    }

    // Set by the caller right after construction, once the stream-level SETUP
    // (which carries streamConnectionID) has told us what key to derive — see
    // AirPlayReceiverServer. One AesCtrKeystreamCipher per connection: the
    // video data channel is a single continuous AES-CTR byte stream split
    // across packets of arbitrary (non-16-byte-aligned) length, so the
    // keystream position has to carry across ReadPacketsAsync's packets
    // rather than restarting at the IV for each one.
    private AesCtrKeystreamCipher? _videoCipher;
    public void SetVideoKeyIv(byte[] key, byte[] iv) => _videoCipher = new AesCtrKeystreamCipher(key, iv);

    private void LogNalStartCode(byte[] data, string label)
    {
        if (data.Length >= 5 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
            Trace($"  {label}: start code NAL trovato (00 00 00 01), tipo NAL = 0x{(data[4] & 0x1F):X2} -> crittografia/derivazione chiave corrette");
        else
            Trace($"  {label}: NESSUN start code NAL all'inizio ({Convert.ToHexString(data.AsSpan(0, Math.Min(8, data.Length)))}...) — qualcosa non torna");
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private void Trace(string message) => Diagnostics?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* best-effort shutdown */ }
        }
        _cts.Dispose();
    }
}
