using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The TCP connection a real device opens after a successful mirroring
/// <c>SETUP</c> — this is where the actual H.264 video arrives. Decrypts
/// real frames (verified against real hardware: ~2200 consecutive packets,
/// one real mirroring session, all correct — see README) and hands the
/// decoded SPS/PPS and each NAL unit to <see cref="ConfigReceived"/>/
/// <see cref="NalReceived"/> for a renderer to consume; decoding pixels and
/// drawing them is that renderer's job, not this class's.
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

    /// <summary>Fires once, when the unencrypted AVCDecoderConfigurationRecord packet arrives — the SPS/PPS a renderer needs before it can decode anything. Args: (sps, pps).</summary>
    public event Action<byte[], byte[]>? ConfigReceived;

    /// <summary>Fires once per decrypted VCL NAL unit (Annex-B-style bytes NOT included — raw NAL, header byte first), in wire order. Args: (nal, isIdr).</summary>
    public event Action<byte[], bool>? NalReceived;

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

            // Confirmed against UxPlay's real raop_rtp_mirror.c: payload_size =
            // byteutils_get_int(packet, 0) — byteutils_get_int is little-endian
            // (byteutils_get_int_be is the separate big-endian variant UxPlay
            // uses elsewhere, e.g. for the NAL length prefix INSIDE a decrypted
            // payload). Previously read big-endian here — never actually
            // exercised against a real packet until tonight, when it decoded a
            // real small payload size as a huge nonsense value and dropped the
            // connection the client had, for the first time ever, opened for real.
            int payloadSize = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
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
                // Unencrypted "SPS+PPS" packet — a real AVCDecoderConfigurationRecord (see AvcDecoderConfig), not a bare NAL pair.
                (byte[] Sps, byte[] Pps)? config = AvcDecoderConfig.TryParse(payload);
                if (config is { } c)
                {
                    Trace($"  SPS/PPS: config valido (SPS {c.Sps.Length} byte, PPS {c.Pps.Length} byte)");
                    ConfigReceived?.Invoke(c.Sps, c.Pps);
                }
                else
                {
                    Trace($"  SPS/PPS: AVCDecoderConfigurationRecord non valido ({Convert.ToHexString(payload.AsSpan(0, Math.Min(8, payload.Length)))}...)");
                }
            }
            else if (payloadTypeHigh == 0x00 && payloadSize > 0)
            {
                byte[] decrypted = _videoCipher!.Transform(payload);
                bool isIdr = payloadTypeLow == 0x10;
                foreach (byte[] nal in AvcDecoderConfig.SplitAvccNalUnits(decrypted))
                    NalReceived?.Invoke(nal, isIdr);
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
